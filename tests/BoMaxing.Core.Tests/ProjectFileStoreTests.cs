using System.IO.Compression;
using System.Text.Json;
using BoMaxing.Core.Project;

namespace BoMaxing.Core.Tests;

public sealed class ProjectFileStoreTests
{
    [Fact]
    public async Task Saves_and_loads_a_versioned_project()
    {
        var project = new ProjectDocument
        {
            Name = "Roundtrip"
        };
        var workflow = project.AddWorkflow("Workflow");
        var node = workflow.AddNode("core.constant", "Value");
        node.Parameters["value"] = JsonSerializer.SerializeToElement(123);
        project.Recipes.Add(new RecipeDefinition
        {
            Name = "Variant A",
            Parameters = new Dictionary<string, JsonElement>
            {
                ["threshold"] = JsonSerializer.SerializeToElement(128)
            }
        });
        project.Hmi.Widgets.Add(new HmiWidgetDefinition
        {
            Kind = HmiWidgetKind.Trend,
            Title = "Cycle time",
            Binding = "workflow.Main.duration",
            Width = 320,
            Height = 180
        });
        var filePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.bomaxing.json");

        try
        {
            var store = new ProjectFileStore();
            await store.SaveAsync(project, filePath);
            var loaded = await store.LoadAsync(filePath);

            Assert.Equal(project.Name, loaded.Name);
            Assert.Equal(ProjectSchema.CurrentVersion, loaded.SchemaVersion);
            Assert.Single(loaded.Recipes);
            Assert.Equal(128, loaded.Recipes[0].Parameters["threshold"].GetInt32());
            Assert.NotNull(loaded.Devices);
            Assert.Single(loaded.Workflows);
            Assert.Equal(123, loaded.Workflows[0].Nodes[0].Parameters["value"].GetInt32());
            Assert.Single(loaded.Hmi.Widgets);
            Assert.Equal(HmiWidgetKind.Trend, loaded.Hmi.Widgets[0].Kind);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public void Run_history_query_and_trend_support_filtering_and_paging()
    {
        var now = new DateTimeOffset(2026, 9, 21, 3, 10, 0, TimeSpan.Zero);
        var entries = new[]
        {
            new WorkflowRunHistoryEntry(now.AddMinutes(-3), "main", "SUCCESS", 10, "ok"),
            new WorkflowRunHistoryEntry(now.AddMinutes(-2), "main", "FAILED", 20, "failed"),
            new WorkflowRunHistoryEntry(now.AddMinutes(-1), "other", "SUCCESS", 30, "ok")
        };

        var page = WorkflowRunHistoryQueryService.Query(
            entries,
            new WorkflowRunHistoryQuery(
                WorkflowId: "main",
                Skip: 0,
                Take: 1));
        var trend = WorkflowRunHistoryQueryService.BuildTrend(
            entries,
            TimeSpan.FromMinutes(5));

        Assert.Equal(2, page.TotalCount);
        Assert.Single(page.Entries);
        Assert.True(page.HasMore);
        Assert.Single(trend);
        Assert.Equal(3, trend[0].RunCount);
        Assert.Equal(2, trend[0].SuccessCount);
        Assert.Equal(20, trend[0].AverageDurationMilliseconds);
    }

    [Fact]
    public async Task Migrates_legacy_project_schema()
    {
        var filePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.bomaxing.json");
        await File.WriteAllTextAsync(filePath, "{\"schemaVersion\":1,\"name\":\"Legacy\"}");

        try
        {
            var loaded = await new ProjectFileStore().LoadAsync(filePath);
            Assert.Equal(ProjectSchema.CurrentVersion, loaded.SchemaVersion);
            Assert.Empty(loaded.Settings);
            Assert.Empty(loaded.Devices);
            Assert.Equal(string.Empty, loaded.Description);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public async Task Deployment_package_contains_manifest_project_and_hashed_files()
    {
        var project = new ProjectDocument { Name = "Deployable" };
        var packagePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.bomaxing.zip");
        try
        {
            var manifest = await new DeploymentPackageWriter().WriteAsync(
                project,
                packagePath,
                new Dictionary<string, byte[]> { ["drivers/demo.txt"] = [1, 2, 3] });

            Assert.Equal(project.Id, manifest.ProjectId);
            Assert.Contains(manifest.Files, file => file.Path == "drivers/demo.txt");
            using var archive = ZipFile.OpenRead(packagePath);
            Assert.NotNull(archive.GetEntry("deployment.manifest.json"));
            Assert.NotNull(archive.GetEntry("project.bomaxing.json"));
            Assert.NotNull(archive.GetEntry("drivers/demo.txt"));
        }
        finally
        {
            File.Delete(packagePath);
        }
    }

    [Fact]
    public async Task Deployment_package_rejects_path_traversal()
    {
        var packagePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.zip");
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                new DeploymentPackageWriter().WriteAsync(
                    new ProjectDocument(),
                    packagePath,
                    new Dictionary<string, byte[]> { ["../outside.txt"] = [1] }));
        }
        finally
        {
            File.Delete(packagePath);
        }
    }

    [Fact]
    public async Task Deployment_verifier_rejects_tampered_file()
    {
        var packagePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.bomaxing.zip");
        var tamperedPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.bomaxing-tampered.zip");
        try
        {
            await new DeploymentPackageWriter().WriteAsync(
                new ProjectDocument { Name = "Verified" },
                packagePath,
                new Dictionary<string, byte[]> { ["drivers/demo.txt"] = [1, 2, 3] });

            using (var input = ZipFile.OpenRead(packagePath))
            using (var output = ZipFile.Open(tamperedPath, ZipArchiveMode.Create))
            {
                foreach (var entry in input.Entries)
                {
                    var copy = output.CreateEntry(entry.FullName);
                    await using var source = entry.Open();
                    await using var target = copy.Open();
                    if (entry.FullName == "drivers/demo.txt")
                    {
                        await target.WriteAsync(new byte[] { 9, 9, 9 });
                    }
                    else
                    {
                        await source.CopyToAsync(target);
                    }
                }
            }

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                new DeploymentPackageVerifier().VerifyAsync(tamperedPath));
        }
        finally
        {
            File.Delete(packagePath);
            File.Delete(tamperedPath);
        }
    }

    [Fact]
    public async Task Deployment_installer_switches_release_and_rolls_back()
    {
        var packageA = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-a.zip");
        var packageB = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-b.zip");
        var installRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var writer = new DeploymentPackageWriter();
            var project = new ProjectDocument { Name = "Deployable" };
            project.AddWorkflow("Main");
            await writer.WriteAsync(project, packageA, new Dictionary<string, byte[]> { ["version.txt"] = "A"u8.ToArray() });
            await writer.WriteAsync(project, packageB, new Dictionary<string, byte[]> { ["version.txt"] = "B"u8.ToArray() });
            var installer = new DeploymentPackageInstaller();

            var first = await installer.InstallAsync(packageA, installRoot);
            var firstPath = await installer.GetCurrentReleasePathAsync(installRoot);
            Assert.Equal(first.CurrentRelease, Path.GetFileName(firstPath));
            Assert.Equal("A", await File.ReadAllTextAsync(Path.Combine(firstPath, "version.txt")));

            var second = await installer.InstallAsync(packageB, installRoot);
            var secondPath = await installer.GetCurrentReleasePathAsync(installRoot);
            Assert.Equal(second.CurrentRelease, Path.GetFileName(secondPath));
            Assert.Equal("B", await File.ReadAllTextAsync(Path.Combine(secondPath, "version.txt")));

            var rolledBack = await installer.RollbackAsync(installRoot);
            var rollbackPath = await installer.GetCurrentReleasePathAsync(installRoot);
            Assert.Equal(rolledBack.CurrentRelease, Path.GetFileName(rollbackPath));
            Assert.Equal("A", await File.ReadAllTextAsync(Path.Combine(rollbackPath, "version.txt")));
        }
        finally
        {
            File.Delete(packageA);
            File.Delete(packageB);
            if (Directory.Exists(installRoot))
            {
                Directory.Delete(installRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Deployment_installer_checks_health_and_prunes_unprotected_releases()
    {
        var packagePaths = Enumerable.Range(1, 3)
            .Select(index => Path.Combine(
                Path.GetTempPath(),
                $"{Guid.NewGuid():N}-{index}.zip"))
            .ToArray();
        var installRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var writer = new DeploymentPackageWriter();
            var project = new ProjectDocument { Name = "Healthy Deployment" };
            project.AddWorkflow("Main");
            for (var index = 0; index < packagePaths.Length; index++)
            {
                await writer.WriteAsync(
                    project,
                    packagePaths[index],
                    new Dictionary<string, byte[]>
                    {
                        ["version.txt"] = [(byte)('A' + index)]
                    });
            }

            var installer = new DeploymentPackageInstaller();
            await installer.InstallAsync(packagePaths[0], installRoot);
            await installer.InstallAsync(packagePaths[1], installRoot);
            await installer.InstallAsync(packagePaths[2], installRoot);

            var health = await installer.CheckHealthAsync(installRoot);
            var removed = await installer.CleanupReleasesAsync(installRoot, keepReleases: 1);

            Assert.True(health.IsHealthy);
            Assert.Equal(project.Id, health.ProjectId);
            Assert.Equal(1, removed);
            Assert.True(Directory.EnumerateDirectories(
                Path.Combine(installRoot, "releases")).Count() >= 2);
        }
        finally
        {
            foreach (var packagePath in packagePaths)
            {
                File.Delete(packagePath);
            }

            if (Directory.Exists(installRoot))
            {
                Directory.Delete(installRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void Deployment_service_generator_writes_platform_definitions()
    {
        var definition = new DeploymentServiceDefinition(
            "bomaxing-runtime",
            "BoMaxing Runtime",
            "BoMaxing industrial vision runtime",
            "/opt/bomaxing/BoMaxing.Service",
            "--install-root /opt/bomaxing",
            "/opt/bomaxing");

        var systemd = DeploymentServiceFileGenerator.Generate(
            definition,
            DeploymentServicePlatform.Systemd);
        var windows = DeploymentServiceFileGenerator.Generate(
            definition,
            DeploymentServicePlatform.Windows);

        Assert.Equal("bomaxing-runtime.service", systemd.FileName);
        Assert.Contains("ExecStart=/opt/bomaxing/BoMaxing.Service --install-root /opt/bomaxing", systemd.Content);
        Assert.Contains("Restart=on-failure", systemd.Content);
        Assert.Equal("install-service.ps1", windows.FileName);
        Assert.Contains("New-Service", windows.Content);
        Assert.Contains("bomaxing-runtime", windows.Content);
    }

    [Fact]
    public async Task Deployment_service_generator_rejects_unsafe_names()
    {
        Assert.Throws<ArgumentException>(() =>
            DeploymentServiceFileGenerator.Generate(
                new DeploymentServiceDefinition(
                    "../runtime",
                    "Runtime",
                    "Runtime",
                    "runtime"),
                DeploymentServicePlatform.Systemd));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            DeploymentServiceFileWriter.WriteAsync(
                new DeploymentServiceFile("../service", "content"),
                Path.GetTempPath()));
    }

    [Fact]
    public async Task Workflow_run_history_roundtrips_atomically_and_keeps_newest_entries()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}", "history.json");
        var projectId = "project-1";
        try
        {
            var store = new WorkflowRunHistoryStore();
            await store.SaveAsync(
                path,
                projectId,
                [
                    new WorkflowRunHistoryEntry(
                        DateTimeOffset.UtcNow.AddMinutes(-1),
                        "workflow-1",
                        "FAILED",
                        12.5,
                        "failed"),
                    new WorkflowRunHistoryEntry(
                        DateTimeOffset.UtcNow,
                        "workflow-1",
                        "SUCCESS",
                        8.25,
                        "success"),
                    new WorkflowRunHistoryEntry(
                        DateTimeOffset.UtcNow,
                        "",
                        "INVALID",
                        1,
                        "ignored")
                ]);

            var loaded = await store.LoadAsync(path, projectId);

            Assert.Equal(2, loaded.Count);
            Assert.Equal("SUCCESS", loaded[0].Status);
            Assert.Equal(8.25, loaded[0].DurationMilliseconds);
            Assert.DoesNotContain(
                Directory.EnumerateFiles(Path.GetDirectoryName(path)!),
                file => file.Contains(".tmp-", StringComparison.Ordinal));
        }
        finally
        {
            var directory = Path.GetDirectoryName(path);
            if (directory is not null && Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
