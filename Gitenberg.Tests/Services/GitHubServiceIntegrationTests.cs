using FluentAssertions;
using Gitenberg.Web.Models;
using Gitenberg.Web.Services;
using Microsoft.Extensions.Configuration;
using Octokit;
using Xunit;

namespace Gitenberg.Tests.Services;

public class GitHubServiceIntegrationTests
{
    private readonly GitHubRepositoryContext _githubContext;
    private readonly GitHubService _gitHubService;

    public GitHubServiceIntegrationTests()
    {
        var config = new ConfigurationBuilder()
                     .AddJsonFile("secrets.json", false, false)
                     .Build();

        var token = config["GitHub:TestToken"];
        var owner = config["GitHub:TestOwner"];
        var repo = config["GitHub:TestRepo"];

        if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(owner) || string.IsNullOrEmpty(repo))
        {
            throw new InvalidOperationException(
                "Тестовые учетные данные GitHub не настроены в User Secrets проекта Gitenberg.Tests."
            );
        }

        _githubContext = new GitHubRepositoryContext(token, owner, repo);
        _gitHubService = new GitHubService();
    }

    [Fact]
    public async Task Note_Lifecycle_PerformsFullCrudOperations()
    {
        var testFileName = $"test-notes/temp_note_{Guid.NewGuid()}.md";

        const string initialContent = "# Тестовая заметка\nСоздано автоматическим интеграционным тестом.";
        const string updatedContent = "# Тестовая заметка\nТекст был успешно обновлен в процессе теста.";

        // Create
        await _gitHubService.CreateOrUpdateNoteAsync(
            _githubContext,
            testFileName,
            initialContent,
            "test: create temporary note"
        );

        // Read
        var readContent = await _gitHubService.GetNoteContentAsync(_githubContext, testFileName);
        readContent.Should().Be(initialContent);

        // Update
        await _gitHubService.CreateOrUpdateNoteAsync(
            _githubContext,
            testFileName,
            updatedContent,
            "test: update temporary note"
        );

        var readUpdatedContent = await _gitHubService.GetNoteContentAsync(_githubContext, testFileName);
        readUpdatedContent.Should().Be(updatedContent);

        // Delete
        await _gitHubService.DeleteNoteAsync(_githubContext, testFileName, "test: cleanup temporary note");

        var act = async () => await _gitHubService.GetNoteContentAsync(_githubContext, testFileName);
        await act.Should().ThrowAsync<NotFoundException>();
    }
}