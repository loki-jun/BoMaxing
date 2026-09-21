namespace BoMaxing.Core.Project;

public static class ProjectSchemaMigrator
{
    public static ProjectDocument Migrate(ProjectDocument project)
    {
        ArgumentNullException.ThrowIfNull(project);

        if (project.SchemaVersion > ProjectSchema.CurrentVersion)
        {
            throw new InvalidDataException(
                $"Project schema version {project.SchemaVersion} is newer than the supported version {ProjectSchema.CurrentVersion}.");
        }

        project.Workflows ??= [];
        project.Settings ??= [];
        project.Description ??= string.Empty;
        project.Devices ??= [];
        project.Recipes ??= [];
        project.Hmi ??= new HmiDefinition();
        project.Hmi.Title ??= "BoMaxing HMI";
        project.Hmi.Widgets ??= [];
        if (project.Hmi.RefreshIntervalMilliseconds <= 0)
        {
            project.Hmi.RefreshIntervalMilliseconds = 500;
        }

        // Version 2 introduced project metadata and runtime settings. Missing
        // values are intentionally initialized so old files remain editable.
        if (project.SchemaVersion < 2)
        {
            project.SchemaVersion = 2;
            project.Touch();
        }

        // Version 3 introduced persisted devices and node layout metadata.
        if (project.SchemaVersion < 3)
        {
            project.SchemaVersion = 3;
            project.Touch();
        }

        // Version 4 introduced recipe definitions for production variants.
        if (project.SchemaVersion < 4)
        {
            project.SchemaVersion = 4;
            project.Touch();
        }

        foreach (var workflow in project.Workflows)
        {
            workflow.Nodes ??= [];
            workflow.Edges ??= [];
            foreach (var node in workflow.Nodes)
            {
                node.Parameters ??= [];
            }
        }

        foreach (var recipe in project.Recipes)
        {
            recipe.Parameters ??= [];
            recipe.Description ??= string.Empty;
        }

        foreach (var widget in project.Hmi.Widgets)
        {
            widget.Settings ??= [];
            widget.Title ??= "Widget";
            widget.Binding ??= string.Empty;
            widget.Width = Math.Max(1, widget.Width);
            widget.Height = Math.Max(1, widget.Height);
        }

        foreach (var device in project.Devices)
        {
            device.Settings ??= [];
            device.FeatureValues ??= [];
        }

        return project;
    }
}
