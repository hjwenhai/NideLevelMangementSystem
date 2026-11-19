namespace Naide.LevelManager.Infrastructure;

internal static class BusinessSchema
{
    internal const string Sql = """
        CREATE TABLE capacity_tables(Id TEXT PRIMARY KEY,Revision INTEGER NOT NULL,Number TEXT NOT NULL COLLATE NOCASE UNIQUE,Name TEXT NOT NULL,Kind INTEGER NOT NULL,HeightUnit INTEGER NOT NULL,VolumeUnit INTEGER NOT NULL,Enabled INTEGER NOT NULL,PointCount INTEGER NOT NULL,ReferenceDensity TEXT NOT NULL) WITHOUT ROWID;
        CREATE TABLE capacity_table_versions(TableId TEXT NOT NULL,Revision INTEGER NOT NULL,Payload TEXT NOT NULL,AtUtcMs INTEGER NOT NULL,PRIMARY KEY(TableId,Revision),FOREIGN KEY(TableId) REFERENCES capacity_tables(Id)) WITHOUT ROWID;
        ALTER TABLE calculation_versions ADD COLUMN Fingerprint TEXT NULL;
        CREATE UNIQUE INDEX IX_calculation_versions_Fingerprint ON calculation_versions(Fingerprint);
        PRAGMA user_version=3;
        """;
}
