namespace Gitenberg.Web.DTOs.Requests;

// Folder suggestions: the token is provided either inline (registration form)
// or by referencing a stored repository (settings form, stored token is used).
public record RepositoryFoldersRequest(
    string RepositoryOwner,
    string RepositoryName,
    string? GitHubToken = null,
    int? RepositoryId = null
);
