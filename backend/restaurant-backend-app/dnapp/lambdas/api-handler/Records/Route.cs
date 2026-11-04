namespace APIHandler.Records;

using APIHandler.Models.Users;

public record Route(string Method, string Template, Roles? RequiredRole = null);
