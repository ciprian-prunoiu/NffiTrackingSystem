CREATE TABLE IF NOT EXISTS NffiPositions (
    Id INTEGER PRIMARY KEY AUTOINCREMENT, UnitId TEXT NOT NULL, UnitName TEXT,
    Latitude REAL NOT NULL, Longitude REAL NOT NULL, Altitude REAL,
    Velocity REAL, Heading REAL, OperationalStatus TEXT,
    ReportedAtUtc TEXT NOT NULL, ReceivedAtUtc TEXT NOT NULL,
    JreapSequenceNumber INTEGER, RawXml TEXT);
CREATE INDEX IF NOT EXISTS IX_NffiPositions_UnitId_ReportedAt ON NffiPositions (UnitId, ReportedAtUtc DESC);
CREATE TABLE IF NOT EXISTS ConnectedUnits (
    UnitId TEXT PRIMARY KEY, UnitName TEXT, LastSeenUtc TEXT NOT NULL,
    LastLatitude REAL, LastLongitude REAL);