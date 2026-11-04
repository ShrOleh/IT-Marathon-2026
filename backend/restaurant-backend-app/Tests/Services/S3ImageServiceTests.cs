using Amazon.Lambda.Core;
using Amazon.S3;
using Amazon.S3.Model;
using APIHandler.Exceptions;
using APIHandler.Services;
using Moq;
using System.Text;

namespace APIHandler.Tests.Services;

public class S3ImageServiceTests
{
    private const int MaxImageBytes = 4 * 1024 * 1024;
    private readonly Mock<IAmazonS3> _s3Client = new();
    private readonly ILambdaLogger _logger = Mock.Of<ILambdaLogger>();

    public static IEnumerable<object[]> SupportedImages()
    {
        yield return [PngImage(), "image/png"];
        yield return [new byte[] { 0xff, 0xd8, 0xff, 0xe0, 0, 2, 0xff, 0xd9 }, "image/jpeg"];
        yield return [Convert.FromBase64String("R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7"), "image/gif"];
        yield return [Encoding.ASCII.GetBytes("RIFF\x10\0\0\0WEBPVP8L\x03\0\0\0"), "image/webp"];
        yield return [Encoding.ASCII.GetBytes("BM").Concat(new byte[24]).ToArray(), "image/bmp"];
        yield return [new byte[] { 0x49, 0x49, 0x2a, 0, 8, 0, 0, 0 }, "image/tiff"];
        yield return [new byte[] { 0x4d, 0x4d, 0, 0x2a, 0, 0, 0, 8 }, "image/tiff"];
        yield return [Encoding.ASCII.GetBytes("qoif").Concat(new byte[] { 0, 0, 0, 1, 0, 0, 0, 1, 3, 0 }).ToArray(), "image/qoi"];
        yield return [Encoding.ASCII.GetBytes("P4\n1 1\n\0"), "image/x-portable-bitmap"];
        yield return [Encoding.ASCII.GetBytes("P5\n1 1\n255\n\0"), "image/x-portable-graymap"];
        yield return [Encoding.ASCII.GetBytes("P6\n1 1\n255\n\0\0\0"), "image/x-portable-pixmap"];
        yield return [new byte[26].Concat(Encoding.ASCII.GetBytes("TRUEVISION-XFILE.\0")).ToArray(), "image/x-tga"];
    }

    [Theory]
    [MemberData(nameof(SupportedImages))]
    public async Task UploadImageAsync_UsesCanonicalContentTypeAndPreservesObject(byte[] image, string expectedContentType)
    {
        PutObjectRequest? uploadedRequest = null;
        byte[]? uploadedBytes = null;
        _s3Client.Setup(client => client.PutObjectAsync(It.IsAny<PutObjectRequest>(), It.IsAny<CancellationToken>()))
            .Callback<PutObjectRequest, CancellationToken>((request, _) =>
            {
                uploadedRequest = request;
                using var copy = new MemoryStream();
                request.InputStream.CopyTo(copy);
                uploadedBytes = copy.ToArray();
            })
            .ReturnsAsync(new PutObjectResponse());
        var service = new S3ImageService(_s3Client.Object);

        var key = await service.UploadImageAsync(image, "private-images", "user-123", _logger, "profile-img");

        Assert.Equal("profile-img/user-123", key);
        Assert.NotNull(uploadedRequest);
        Assert.Equal("private-images", uploadedRequest.BucketName);
        Assert.Equal(key, uploadedRequest.Key);
        Assert.Equal(expectedContentType, uploadedRequest.ContentType);
        Assert.Equal(image, uploadedBytes);
        _s3Client.Verify(client => client.PutObjectAsync(It.IsAny<PutObjectRequest>(), It.IsAny<CancellationToken>()), Times.Once);
        _s3Client.VerifyNoOtherCalls();
    }

    public static IEnumerable<object[]> RejectedImages()
    {
        yield return [Array.Empty<byte>()];
        yield return [Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\"></svg>")];
        yield return [Encoding.UTF8.GetBytes("<html>not an image</html>")];
        yield return [new byte[] { 0xff, 0xd8, 0xff }];
        yield return [Encoding.ASCII.GetBytes("RIFF\0\0\0\0WAVEfmt ")];
    }

    [Theory]
    [MemberData(nameof(RejectedImages))]
    public async Task UploadImageAsync_RejectsUnsupportedOrTruncatedInputWithoutS3(byte[] image)
    {
        var service = new S3ImageService(_s3Client.Object);

        var exception = await Assert.ThrowsAsync<ValidationException>(() =>
            service.UploadImageAsync(image, "images", "image", _logger));

        Assert.Contains("PNG", exception.Message);
        _s3Client.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task UploadImageAsync_RejectsOversizedImageWithoutS3()
    {
        var image = new byte[MaxImageBytes + 1];
        PngImage().CopyTo(image, 0);
        var service = new S3ImageService(_s3Client.Object);

        var exception = await Assert.ThrowsAsync<ValidationException>(() =>
            service.UploadImageAsync(image, "images", "image", _logger));

        Assert.Contains("4 MiB", exception.Message);
        _s3Client.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task GetImageKey_RejectsOversizedBase64WithoutS3()
    {
        var image = new byte[MaxImageBytes + 1];
        PngImage().CopyTo(image, 0);
        var service = new S3ImageService(_s3Client.Object);

        var exception = await Assert.ThrowsAsync<ValidationException>(() =>
            service.GetImageKey(Convert.ToBase64String(image), "images", "profile-img", "user-123", _logger));

        Assert.Contains("4 MiB", exception.Message);
        _s3Client.VerifyNoOtherCalls();
    }

    [Fact]
    public void GetImageBytesFromBase64_AcceptsImageAtByteLimit()
    {
        var image = new byte[MaxImageBytes];
        PngImage().CopyTo(image, 0);

        var decodedImage = S3ImageService.GetImageBytesFromBase64(Convert.ToBase64String(image));

        Assert.Equal(image, decodedImage);
    }

    [Fact]
    public async Task GetImageKey_DoesNotTrustDataUrlContentType()
    {
        var image = PngImage();
        _s3Client.Setup(client => client.PutObjectAsync(
                It.Is<PutObjectRequest>(request => request.ContentType == "image/png"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PutObjectResponse());
        var service = new S3ImageService(_s3Client.Object);

        var key = await service.GetImageKey($"data:text/html;base64,{Convert.ToBase64String(image)}",
            "images", "profile-img", "user-123", _logger);

        Assert.Equal("profile-img/user-123", key);
        _s3Client.Verify(client => client.PutObjectAsync(
            It.Is<PutObjectRequest>(request => request.ContentType == "image/png"), It.IsAny<CancellationToken>()), Times.Once);
        _s3Client.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("not-base64")]
    [InlineData("")]
    [InlineData("data:image/png;base64,PHN2Zz48L3N2Zz4=")]
    public void GetImageBytesFromBase64_RejectsInvalidDataBeforeReplacingAnImage(string input)
    {
        Assert.Throws<ValidationException>(() => S3ImageService.GetImageBytesFromBase64(input));
    }

    private static byte[] PngImage() => Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Y9ZgmkAAAAASUVORK5CYII=");
}
