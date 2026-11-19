namespace Naide.LevelManager.Infrastructure;

internal static class HistorySchema
{
    internal const string Sql = """
        CREATE TABLE database_properties(Key TEXT PRIMARY KEY,Value TEXT NOT NULL) WITHOUT ROWID;
        INSERT INTO database_properties VALUES('profile','operational');
        CREATE TABLE history_tanks(Id INTEGER PRIMARY KEY,TankId TEXT NOT NULL UNIQUE);
        CREATE TABLE history_sessions(Id INTEGER PRIMARY KEY,SessionId TEXT NOT NULL UNIQUE);
        CREATE TABLE calculation_versions(Id INTEGER PRIMARY KEY,Payload TEXT NOT NULL,AtUtcMs INTEGER NOT NULL);
        INSERT INTO calculation_versions VALUES(0,'{"kind":"unconfigured"}',0);
        CREATE TABLE history_samples(
            TankKey INTEGER NOT NULL REFERENCES history_tanks(Id),AtUtcMs INTEGER NOT NULL,
            ConfigurationRevision INTEGER NOT NULL REFERENCES configuration_versions(Revision),
            CalculationRevision INTEGER NOT NULL REFERENCES calculation_versions(Id),
            SessionKey INTEGER NOT NULL REFERENCES history_sessions(Id),
            ReceivedAtUtcMs INTEGER NULL,SourceAtUtcMs INTEGER NULL,SourceTimeConfirmed INTEGER NOT NULL,
            DeviceDataAgeMs INTEGER NULL,
            LevelMicrometres INTEGER NULL,InterfaceMicrometres INTEGER NULL,
            InstantMilliC INTEGER NULL,AverageMilliC INTEGER NULL,AverageCompletedAtUtcMs INTEGER NULL,
            DensityMilliKgM3 INTEGER NULL,CurrentVolumeMicroM3 INTEGER NULL,NetVolumeMicroM3 INTEGER NULL,MassMicroT INTEGER NULL,
            QualityBits INTEGER NOT NULL,InstrumentStatus INTEGER NULL,InstrumentFault INTEGER NULL,
            RawQuality INTEGER NOT NULL,RawValidity INTEGER NOT NULL,DiagnosticCode TEXT NOT NULL,
            AtsPoints BLOB NULL,
            PRIMARY KEY(TankKey,AtUtcMs)
        ) WITHOUT ROWID;
        CREATE INDEX IX_history_samples_AtUtcMs ON history_samples(AtUtcMs,TankKey);
        CREATE TABLE history_record_pins(
            OwnerId TEXT NOT NULL,TankKey INTEGER NOT NULL,AtUtcMs INTEGER NOT NULL,
            PRIMARY KEY(OwnerId,TankKey,AtUtcMs),
            FOREIGN KEY(TankKey,AtUtcMs) REFERENCES history_samples(TankKey,AtUtcMs) ON DELETE RESTRICT
        ) WITHOUT ROWID;
        CREATE INDEX IX_history_pins_Record ON history_record_pins(TankKey,AtUtcMs);
        PRAGMA user_version=2;
        """;
}
