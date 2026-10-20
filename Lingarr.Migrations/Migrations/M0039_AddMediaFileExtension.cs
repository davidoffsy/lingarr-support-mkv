using FluentMigrator;

namespace Lingarr.Migrations.Migrations;

/// <summary>
/// Preserves the video container extension independently of the subtitle-matching file stem.
/// Existing media stays unknown until the next Radarr or Sonarr sync.
/// </summary>
[Migration(39)]
public class M0039_AddMediaFileExtension : Migration
{
    public override void Up()
    {
        foreach (var table in new[] { "movies", "episodes" })
        {
            if (!Schema.Table(table).Column("file_extension").Exists())
            {
                Alter.Table(table)
                    .AddColumn("file_extension")
                    .AsCustom("TEXT")
                    .Nullable();
            }
        }
    }

    public override void Down()
    {
        foreach (var table in new[] { "movies", "episodes" })
        {
            if (Schema.Table(table).Column("file_extension").Exists())
            {
                Delete.Column("file_extension").FromTable(table);
            }
        }
    }
}