using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.IO;
using AstraCat;

namespace AstraCat.Tests;

[TestClass]
public class ProjectRepositoryTests
{
    [TestMethod]
    public void ProjectRepository_Paths_AreConsistent()
    {
        var repo = ProjectRepository.Shared;
        var projectId = "test-project-123";

        var projectDir = repo.ProjectDirectory(projectId);
        var cuesPath = repo.WorkspaceCuesPath(projectId);
        var statePath = repo.WorkspaceCueStatePath(projectId);
        var transPath = repo.ProjectTranslationCachePath(projectId);

        Assert.IsTrue(cuesPath.StartsWith(projectDir), "Cues path must reside within the project directory");
        Assert.IsTrue(statePath.StartsWith(projectDir), "State path must reside within the project directory");
        Assert.IsTrue(transPath.StartsWith(projectDir), "Translation cache path must reside within the project directory");
    }

    [TestMethod]
    public void ProjectRepository_GetProjectGate_ReturnsSameSemaphoreForSameProject()
    {
        var repo = ProjectRepository.Shared;
        var gate1 = repo.GetProjectGate("proj-abc");
        var gate2 = repo.GetProjectGate("PROJ-ABC"); // case insensitive

        Assert.AreSame(gate1, gate2, "Gate should be case-insensitively shared for the same project ID");
    }
}
