using FluentMigrator;

namespace Lingarr.Migrations.Migrations;

/// <summary>
/// Seeds the optional inline ideographic-period replacement. Off by default so
/// existing installs keep writing translated cues unchanged.
/// </summary>
[Migration(40)]
public class M0040_SeedReplaceInlineIdeographicFullStop : Migration
{
    public override void Up()
    {
        Execute.WithConnection((connection, transaction) =>
            GuardedSettingSeed.InsertMissing(connection, transaction, new Dictionary<string, string>
            {
                ["replace_inline_ideographic_full_stop"] = "false"
            }));
    }

    public override void Down()
    {
        Execute.WithConnection((connection, transaction) =>
            GuardedSettingSeed.DeleteIfCurrentValue(connection, transaction, new Dictionary<string, string>
            {
                ["replace_inline_ideographic_full_stop"] = "false"
            }));
    }
}
