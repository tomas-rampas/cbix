/*
    CBIX operational table: dbo.document_registry
    ---------------------------------------------
    Design references:
      - 5.1  Ingestion: "The executor computes a content hash for idempotency and
             deduplication, records the document in a registry table".
      - 6    Data model: document_registry is named in the operational-tables list
             ("... plus operational tables document_registry, extraction_runs,
             review_queue, category_mappings ... and golden_set"). The published
             tables are sketched there in full; this file supplies the operational
             one in the same T-SQL style and with the same constraint discipline.
      - 8    Idempotency: "Content hash deduplicates re-submissions".
      - 9    Duplicate submission -> detected by "Registry hash match", handled as
             "No-op, audit log entry".

    Status: schema only. The SQL Server wiring (connection, migration runner,
    the IDocumentRegistry implementation that targets this table) lands in
    Sprint 03; story S01-10 ships the shape so the in-memory registry and the
    eventual SQL one cannot drift apart unnoticed. The in-process counterpart of
    a row here is Cbix.Core.Ingest.DocumentRegistryEntry.

    The audit trail of submissions - including the duplicate no-op required by
    design 9 - is deliberately NOT this table: registry rows are written once and
    never updated, so repeat submissions are recorded through the
    Cbix.Core.Ingest.IIngestAuditLog port, whose durable table ships with the same
    Sprint 03 persistence work.
*/

CREATE TABLE dbo.document_registry (
    -- Identity, and the value every downstream table joins on. It is the canonical
    -- '<hash_algorithm>:<content_hash>' rendering of the digest, so identity is derived
    -- from content and can never be chosen by whoever submitted the document. This is
    -- the exact string carried in DocumentReference.DocumentId.
    --
    -- Width: 80 is deliberately far narrower than the 256 DocumentReference will accept
    -- for a document id in general. Only DERIVED ids reach this table - the application
    -- has exactly one place that mints one (DocumentIngestService), and it always emits
    -- 'sha256:' plus 64 hex characters, i.e. 71. The 80 leaves room for a longer
    -- algorithm name without leaving room for an arbitrary caller-chosen string, so a
    -- future code path that tried to register a free-form identity would fail loudly here
    -- rather than quietly store something the ck_..._identity CHECK below would then have
    -- to catch.
    document_id        varchar(80)    COLLATE Latin1_General_BIN2 NOT NULL,

    -- The digest, split out so it is queryable on its own terms. The algorithm travels
    -- with the value because a bare hex string stops being self-describing the day the
    -- digest changes.
    hash_algorithm     varchar(20)    COLLATE Latin1_General_BIN2 NOT NULL,
    content_hash       char(64)       COLLATE Latin1_General_BIN2 NOT NULL,

    -- The document as it was read at first registration. file_name is a bare display
    -- name (no path separators - enforced by DocumentReference before a row is built);
    -- source_location is the FULLY RESOLVED file:// location the bytes were actually read
    -- from, after every path component was followed to its final link target. Storing the
    -- submitted path instead would let provenance name a location whose bytes were never
    -- the ones hashed. It is retained for audit, not for re-reading.
    --
    -- Widths are the application's validated bounds, not guesses:
    --   file_name       260 - the practical per-component path limit.
    --   media_type      255 - the media-type grammar has no fixed limit, so this is the
    --                   conventional header-field bound; DocumentReference validates the
    --                   grammar, this bounds the length. Raised from 120: a type with
    --                   parameters (e.g. 'application/pdf; version=...') is legitimate and
    --                   silent truncation of a validated value is worse than a wide column.
    --   source_location 1000 - matches the app-side bound. Ingest refuses UNC and device
    --                   paths outright and every stored location lies under one configured
    --                   ingest root, so a resolved local path cannot approach this.
    file_name          nvarchar(260)  NOT NULL,
    media_type         varchar(255)   NOT NULL,
    byte_length        bigint         NOT NULL,
    source_location    nvarchar(1000) NOT NULL,

    -- First registration only. A duplicate submission never updates this row, so this
    -- column keeps meaning "when these bytes first entered the pipeline" permanently.
    first_seen_utc     datetime2      NOT NULL,

    CONSTRAINT pk_document_registry PRIMARY KEY (document_id),

    -- The primary key IS the deduplication gate: two submissions of the same bytes
    -- produce the same document_id, so the second insert is a duplicate-key violation
    -- rather than a second row. No separate unique index on the hash earns its keep,
    -- and no lookup-then-insert pattern is needed (or permitted) to detect a duplicate.

    -- Only the algorithm the service can actually recompute may be stored: an identity
    -- nothing can re-derive from the bytes is not an identity.
    CONSTRAINT ck_document_registry_algorithm
        CHECK (hash_algorithm = 'sha256'),

    -- Lower-case hex only. The binary collation on the three identity columns is what
    -- makes this check case-sensitive; under the usual case-insensitive database
    -- collation 'A1B2...' would pass and the same document would have two spellings of
    -- one key. Declaring all three BIN2 also keeps the identity CHECK below free of a
    -- collation conflict, which mixing a BIN2 column with a default-collation one in a
    -- single concatenation would otherwise raise.
    CONSTRAINT ck_document_registry_hash_format
        CHECK (content_hash NOT LIKE '%[^0-9a-f]%'),

    -- The derivation is an enforced invariant, not a convention the application is
    -- trusted to keep: a row whose document_id does not equal its own hash cannot exist.
    --
    -- Nuance, recorded so nobody rediscovers it as a bug: T-SQL '=' ignores TRAILING
    -- SPACES, so 'sha256:ab..cd   ' would satisfy this CHECK against a document_id with
    -- none. It is cosmetic here rather than exploitable, because the two spellings collapse
    -- to one value under the primary key for exactly the same reason - they compare equal,
    -- so the second insert is a duplicate-key violation, not a second identity. If a future
    -- column ever needs trailing-space-exact comparison, DATALENGTH() is the operator that
    -- does not ignore them.
    CONSTRAINT ck_document_registry_identity
        CHECK (document_id = hash_algorithm + ':' + content_hash),

    -- A zero-byte submission is not a document; catching it here keeps the "hash of
    -- nothing" identity (SHA-256 of the empty string) out of the registry entirely.
    CONSTRAINT ck_document_registry_byte_length
        CHECK (byte_length > 0)
);

/*
    Write-once, enforced by permissions rather than by convention
    -------------------------------------------------------------
    "A duplicate submission never updates this row" is a property the application
    currently keeps voluntarily. Voluntary is not the same as enforced: an UPDATE run
    by anything holding the ingest credential would silently rewrite first_seen_utc,
    and every audit answer keyed on "when did these bytes first enter the pipeline"
    would change with no trace that it had.

    The permission plan below is written here, next to the table, rather than left
    implied. Principals are created with the rest of the deployment in Sprint 03, so
    the statements are commented; the intent is not.

    Least privilege for the ingest service principal - INSERT and SELECT only:

        -- GRANT SELECT, INSERT ON dbo.document_registry TO [cbix_ingest];
        -- DENY  UPDATE, DELETE ON dbo.document_registry TO [cbix_ingest];

    Read-only for everything downstream (validator, publisher, review UI, reporting):

        -- GRANT SELECT ON dbo.document_registry TO [cbix_reader];

    DENY is used rather than simply withholding GRANT because DENY survives a principal
    later joining a role that grants UPDATE more broadly - it is the form that cannot be
    undone by an unrelated permission change elsewhere.

    Correcting a registry row is therefore a deliberate, privileged, auditable act by a
    DBA, which is the correct cost for editing the identity table of an audit-grade
    pipeline. A trigger raising an error on UPDATE/DELETE is the alternative if the
    estate's standard is trigger-based rather than permission-based; it is second choice
    because a trigger can be disabled by anyone who can ALTER TABLE.
*/
