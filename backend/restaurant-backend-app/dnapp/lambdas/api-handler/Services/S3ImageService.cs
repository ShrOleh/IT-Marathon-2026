using Amazon.Lambda.Core;
using Amazon.S3;
using Amazon.S3.Model;
using APIHandler.Constants;
using APIHandler.Exceptions;
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace APIHandler.Services;

public class S3ImageService
{
    private const int MaxImageBytes = 4 * 1024 * 1024;
    private const int MaxBase64Characters = ((MaxImageBytes + 2) / 3) * 4;
    private const string ImageSizeError = "Images must be 4 MiB or smaller.";
    private const string ImageFormatError = "Upload a PNG, JPEG, GIF, WebP, BMP, TIFF, QOI, Netpbm, or TGA image.";
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];
    private readonly IAmazonS3 _s3Client;

    public S3ImageService(IAmazonS3 s3Client)
    {
        _s3Client = s3Client;
    }

    public async Task<string> UploadImageAsync(byte[] imageBytes, string bucketName, string fileName, ILambdaLogger logger,
        string? folder = null)
    {
        string contentType = GetImageContentType(imageBytes);
        using var stream = new MemoryStream(imageBytes);
        var key = string.IsNullOrWhiteSpace(folder)
            ? $"{fileName}"
            : $"{folder}/{fileName}";

        var request = new PutObjectRequest
        {
            BucketName = bucketName,
            Key = key,
            InputStream = stream,
            ContentType = contentType
        };

        await _s3Client.PutObjectAsync(request);

        return request.Key;
    }

    public string GetPublicUrl(string bucketName, string imageKey)
    {
        return $"https://{bucketName}.s3.{ResourceNames.Region}.amazonaws.com/{imageKey}";
    }

    public string GetImageUrl(string bucketName, string imageKey)
    {
        var request = new GetPreSignedUrlRequest
        {
            BucketName = bucketName,
            Key = imageKey,
            Expires = DateTime.UtcNow.AddMinutes(10)
        };
        return _s3Client.GetPreSignedURL(request);
    }

    public string GetPreSignedImageUrl(string bucketName, string imageKey)
    {
        var request = new GetPreSignedUrlRequest
        {
            BucketName = bucketName,
            Key = imageKey,
            Expires = DateTime.UtcNow.AddHours(1),
        };
        return _s3Client.GetPreSignedURL(request);
    }

    public static byte[] GetImageBytesFromBase64(string base64String)
    {
        if (string.IsNullOrWhiteSpace(base64String))
        {
            throw new ValidationException(ImageFormatError);
        }

        int commaIndex = base64String.IndexOf(',');
        var payload = base64String.AsSpan(commaIndex + 1);
        if (payload.Length > MaxBase64Characters)
        {
            throw new ValidationException(ImageSizeError);
        }

        int characterCount = 0;
        foreach (char character in payload)
        {
            if (!char.IsWhiteSpace(character)) characterCount++;
        }

        var trimmedPayload = payload.TrimEnd();
        int padding = trimmedPayload.EndsWith("==") ? 2 : trimmedPayload.EndsWith("=") ? 1 : 0;
        if ((long)characterCount / 4 * 3 - padding > MaxImageBytes)
        {
            throw new ValidationException(ImageSizeError);
        }

        byte[] imageBytes;
        try
        {
            imageBytes = Convert.FromBase64String(payload.ToString());
        }
        catch (FormatException)
        {
            throw new ValidationException("The uploaded image data is invalid.");
        }

        // Validate before callers fetch or replace an existing image in S3.
        GetImageContentType(imageBytes);
        return imageBytes;
    }

    public async Task<string> GetImageKey(string imageBase64, string bucketName, string folderName, string fileName, ILambdaLogger logger)
    {
        var imageBytes = GetImageBytesFromBase64(imageBase64);

        var imageKey =
            await UploadImageAsync(imageBytes, bucketName, fileName, logger, folderName);
        return imageKey;
    }

    public async Task<byte[]> DownloadImageAsync(string bucketName, string key)
    {
        var response = await _s3Client.GetObjectAsync(bucketName, key);
        using var ms = new MemoryStream();
        await response.ResponseStream.CopyToAsync(ms);
        return ms.ToArray();
    }

    public async Task DeleteImageAsync(string bucketName, string key)
    {
        await _s3Client.DeleteObjectAsync(bucketName, key);
    }

    public static bool CompareImagesHash(byte[] a, byte[] b)
    {
        using var sha = SHA256.Create();
        return sha.ComputeHash(a).SequenceEqual(sha.ComputeHash(b));
    }

    private static string GetImageContentType(byte[] imgData)
    {
        if (imgData is null || imgData.Length == 0)
        {
            throw new ValidationException(ImageFormatError);
        }
        if (imgData.Length > MaxImageBytes)
        {
            throw new ValidationException(ImageSizeError);
        }

        // Identify allowed raster formats using bounded signatures only. This is
        // MIME detection, not full image validation; no pixels or metadata are decoded.
        ReadOnlySpan<byte> data = imgData;
        if (data.Length >= 24 && data.StartsWith(PngSignature) && data.Slice(12, 4).SequenceEqual("IHDR"u8))
            return "image/png";
        if (data.Length >= 4 && data[0] == 0xff && data[1] == 0xd8 && data[2] == 0xff)
            return "image/jpeg";
        if (data.Length >= 13 && (data.StartsWith("GIF87a"u8) || data.StartsWith("GIF89a"u8)))
            return "image/gif";
        if (data.Length >= 16 && data.StartsWith("RIFF"u8) && data.Slice(8, 4).SequenceEqual("WEBP"u8)
            && (data.Slice(12, 4).SequenceEqual("VP8 "u8) || data.Slice(12, 4).SequenceEqual("VP8L"u8)
                || data.Slice(12, 4).SequenceEqual("VP8X"u8)))
            return "image/webp";
        if (data.Length >= 26 && data.StartsWith("BM"u8))
            return "image/bmp";
        if (data.Length >= 8 && (data.StartsWith("II\x2a\0"u8) || data.StartsWith("MM\0\x2a"u8)))
            return "image/tiff";
        if (data.Length >= 16 && (data.StartsWith("II\x2b\0"u8) || data.StartsWith("MM\0\x2b"u8)))
            return "image/tiff";
        if (data.Length >= 14 && data.StartsWith("qoif"u8) && data[12] is 3 or 4 && data[13] <= 1)
            return "image/qoi";
        if (data.Length >= 3 && data[0] == 'P' && data[2] is 9 or 10 or 11 or 12 or 13 or 32)
        {
            if (data[1] is (byte)'1' or (byte)'4') return "image/x-portable-bitmap";
            if (data[1] is (byte)'2' or (byte)'5') return "image/x-portable-graymap";
            if (data[1] is (byte)'3' or (byte)'6') return "image/x-portable-pixmap";
        }
        if (data.Length >= 44 && data.EndsWith("TRUEVISION-XFILE.\0"u8))
            return "image/x-tga";

        throw new ValidationException(ImageFormatError);
    }
}
