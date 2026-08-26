using GLoom.Survey;
using Xunit;

namespace GLoom.Survey.Tests;

public class SurveySchemaLoaderTests
{
    private const string ProjectSchema = """
    {
      "schemaVersion": 1,
      "id": "cdm-cartagena/1.0",
      "core": [],
      "categories": [ { "id": "wall", "label": "Muro", "revit": "Walls", "fields": [] } ],
      "rules": [ { "id": "p-muro", "kind": "glob", "pattern": "*MURO*", "category": "wall" } ]
    }
    """;

    [Fact]
    public void With_no_project_file_the_built_in_schema_is_used_so_the_components_work_with_no_setup()
    {
        using var repo = TempRepo.WithGitDirectory();
        var loaded = SurveySchemaLoader.Load(null, repo.Dir("Coding"), force: true);

        Assert.True(loaded.IsBuiltIn);
        Assert.Equal("built-in", loaded.Source);
        Assert.Equal("gloom-survey/1.0", loaded.Schema.Id);
    }

    [Fact]
    public void Outside_a_repository_the_built_in_schema_is_used()
    {
        using var repo = TempRepo.WithoutGit();
        Assert.True(SurveySchemaLoader.Load(null, repo.Dir("Coding"), force: true).IsBuiltIn);
    }

    [Fact]
    public void A_projects_own_file_wins()
    {
        using var repo = TempRepo.WithGitDirectory();
        repo.File_(".gloom/survey-schema.json", ProjectSchema);

        var loaded = SurveySchemaLoader.Load(null, repo.Dir("Coding"), force: true);

        Assert.False(loaded.IsBuiltIn);
        Assert.Equal("cdm-cartagena/1.0", loaded.Schema.Id);
        Assert.Equal(repo.SchemaPath, loaded.Source);
        Assert.Equal("p-muro", loaded.Matcher.Match("Muros")!.Rule.Id);
    }

    [Fact]
    public void The_schema_is_found_from_a_definition_nested_well_below_the_root()
    {
        using var repo = TempRepo.WithGitDirectory();
        repo.File_(".gloom/survey-schema.json", ProjectSchema);
        var definition = repo.File_("Coding/Definitions/survey.gh", "x");

        Assert.Equal("cdm-cartagena/1.0", SurveySchemaLoader.Load(null, definition, force: true).Schema.Id);
    }

    [Fact]
    public void An_explicit_path_wins_over_the_projects_own_file()
    {
        using var repo = TempRepo.WithGitDirectory();
        repo.File_(".gloom/survey-schema.json", ProjectSchema);
        var other = repo.File_("elsewhere/other-schema.json", ProjectSchema.Replace("cdm-cartagena/1.0", "explicit/2.0"));

        Assert.Equal("explicit/2.0", SurveySchemaLoader.Load(other, repo.Root, force: true).Schema.Id);
    }

    [Fact]
    public void An_explicit_path_that_does_not_exist_falls_back_rather_than_failing_the_solve()
    {
        using var repo = TempRepo.WithGitDirectory();
        var missing = Path.Combine(repo.Root, "nope.json");

        Assert.True(SurveySchemaLoader.Load(missing, repo.Root, force: true).IsBuiltIn);
    }

    [Fact]
    public void An_explicit_path_is_unquoted_and_trimmed_because_it_arrives_from_a_panel()
    {
        using var repo = TempRepo.WithGitDirectory();
        var path = repo.File_("elsewhere/other-schema.json", ProjectSchema);

        Assert.False(SurveySchemaLoader.Load($"  \"{path}\"  ", repo.Root, force: true).IsBuiltIn);
    }

    [Fact]
    public void A_malformed_project_file_falls_back_to_the_built_in_and_says_which_file_it_was()
    {
        using var repo = TempRepo.WithGitDirectory();
        repo.File_(".gloom/survey-schema.json", "{ this is not json");

        var loaded = SurveySchemaLoader.Load(null, repo.Root, force: true);

        Assert.True(loaded.IsBuiltIn);
        Assert.Contains(loaded.Issues, i => i.Kind == "unreadable");
        Assert.Contains(loaded.Issues, i => i.Kind == "fallback" && i.Where == repo.SchemaPath);
    }

    [Fact]
    public void A_parseable_but_invalid_project_file_is_still_used_with_its_findings_attached()
    {
        // One bad rule should not cost the architect the other thirty-nine.
        using var repo = TempRepo.WithGitDirectory();
        const string ghost = ",\n{ \"id\": \"p-ghost\", \"kind\": \"glob\", \"pattern\": \"*X*\", \"category\": \"nonexistent\" }";
        repo.File_(".gloom/survey-schema.json", ProjectSchema.Replace("\"category\": \"wall\" }", "\"category\": \"wall\" }" + ghost));

        var loaded = SurveySchemaLoader.Load(null, repo.Root, force: true);

        Assert.False(loaded.IsBuiltIn);
        Assert.Contains(loaded.Issues, i => i.Kind == "unknown-category");
        Assert.NotNull(loaded.Matcher.Match("Muros"));
    }

    [Fact]
    public void A_schema_is_read_once_per_edit_never_once_per_solve()
    {
        using var repo = TempRepo.WithGitDirectory();
        repo.File_(".gloom/survey-schema.json", ProjectSchema);

        var first = SurveySchemaLoader.Load(null, repo.Root, force: true);
        var second = SurveySchemaLoader.Load(null, repo.Root);

        Assert.Same(first, second);
    }

    [Fact]
    public void Editing_the_file_invalidates_the_memo()
    {
        using var repo = TempRepo.WithGitDirectory();
        repo.File_(".gloom/survey-schema.json", ProjectSchema);
        var first = SurveySchemaLoader.Load(null, repo.Root, force: true);

        repo.File_(".gloom/survey-schema.json", ProjectSchema.Replace("cdm-cartagena/1.0", "cdm-cartagena/1.1-longer"));
        var second = SurveySchemaLoader.Load(null, repo.Root);

        Assert.NotSame(first, second);
        Assert.Equal("cdm-cartagena/1.1-longer", second.Schema.Id);
    }

    [Fact]
    public void The_built_in_schema_is_materialised_once()
    {
        Assert.Same(SurveySchemaLoader.BuiltIn(), SurveySchemaLoader.BuiltIn());
    }

    [Fact]
    public void Identity_is_twelve_hex_characters_of_the_file_text()
    {
        using var repo = TempRepo.WithGitDirectory();
        repo.File_(".gloom/survey-schema.json", ProjectSchema);

        var hash = SurveySchemaLoader.Load(null, repo.Root, force: true).Hash;

        Assert.Equal(12, hash.Length);
        Assert.All(hash, c => Assert.Contains(c, "0123456789abcdef"));
    }

    [Fact]
    public void The_same_text_hashes_the_same_and_different_text_does_not()
    {
        using var a = TempRepo.WithGitDirectory();
        using var b = TempRepo.WithGitDirectory();
        using var c = TempRepo.WithGitDirectory();

        a.File_(".gloom/survey-schema.json", ProjectSchema);
        b.File_(".gloom/survey-schema.json", ProjectSchema);
        c.File_(".gloom/survey-schema.json", ProjectSchema.Replace("cdm-cartagena/1.0", "other/1.0"));

        var first = SurveySchemaLoader.Load(null, a.Root, force: true).Hash;
        var same = SurveySchemaLoader.Load(null, b.Root, force: true).Hash;
        var different = SurveySchemaLoader.Load(null, c.Root, force: true).Hash;

        Assert.Equal(first, same);
        Assert.NotEqual(first, different);
    }

    [Fact]
    public void Version_is_the_pair_that_gets_stamped_onto_every_object()
    {
        using var repo = TempRepo.WithGitDirectory();
        repo.File_(".gloom/survey-schema.json", ProjectSchema);

        var loaded = SurveySchemaLoader.Load(null, repo.Root, force: true);
        Assert.Equal($"cdm-cartagena/1.0@{loaded.Hash}", loaded.Version);
    }

    [Fact]
    public void The_expected_path_is_offered_whether_or_not_the_file_is_there()
    {
        using var repo = TempRepo.WithGitDirectory();
        Assert.Equal(repo.SchemaPath, SurveySchemaLoader.ExpectedPathFor(repo.Dir("Coding")));
    }

    [Fact]
    public void There_is_no_expected_path_outside_a_repository()
    {
        using var repo = TempRepo.WithoutGit();
        Assert.Null(SurveySchemaLoader.ExpectedPathFor(repo.Root));
    }

    [Fact]
    public void An_unsaved_definition_still_resolves_a_project_schema()
    {
        // Grasshopper hands out a file path for a definition that has never been saved;
        // the schema should still be found rather than the project silently getting the
        // built-in vocabulary.
        using var repo = TempRepo.WithGitDirectory();
        repo.File_(".gloom/survey-schema.json", ProjectSchema);
        repo.Dir("Coding");

        var unsaved = Path.Combine(repo.Root, "Coding", "never-saved.gh");
        Assert.Equal("cdm-cartagena/1.0", SurveySchemaLoader.Load(null, unsaved, force: true).Schema.Id);
    }
}
