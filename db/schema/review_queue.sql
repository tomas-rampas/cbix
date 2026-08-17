/*
    CBIX operational table: dbo.review_queue
    ----------------------------------------
    Design references:
      - 4    Pipeline diagram: the validator's and triage's conditional edges route to
             human review, and an approved or corrected document rejoins the publish path.
      - 5.3  Triage: "Conditional edges use this ... to short-circuit non-CBTI documents
             to the review queue rather than guessing."
      - 5.7  Human review: "Every correction is appended to the golden set - review is the
             improvement flywheel, not just a safety net."
      - 6    Data model: review_queue is named in the operational-tables list ("... plus
             operational tables document_registry, extraction_runs, review_queue,
             category_mappings ... and golden_set"). The published tables are sketched
             there in full; this file supplies the operational one in the same T-SQL style
             and with the same constraint discipline as document_registry.
      - 9    "New/unknown layout family" -> detected by "Triage confidence below
             threshold", handled as "Route whole document to review; add few-shots for the
             family".

    Status: schema only. The SQL Server wiring (connection, migration runner, the
    IReviewQueue implementation that targets this table) lands in Sprint 03 with the
    checkpoint store and the HITL pause/resume, and story S01-15 ships the shape so the
    in-memory queue and the eventual SQL one cannot drift apart unnoticed. The in-process
    counterpart of a row here is Cbix.Core.Review.ReviewQueueEntry.

    SCOPE, stated so the table is not mistaken for the whole feature. Sprint 01 writes rows
    and nothing reads them: the routing edge out of triage fires, the run ends with the
    ReviewQueued disposition, and the row waits. The claim/resolve columns below are
    deliberately present and deliberately unused - a reviewer workflow that had to ALTER
    this table to record who worked a row would be a migration on a live queue, and the
    columns cost nothing empty.
*/

CREATE TABLE dbo.review_queue (
    -- Surrogate identity, unlike document_registry's derived one, and the difference is real:
    -- a document can be queued more than once over its life (triage today, the validator in
    -- Sprint 02, a later version of the same document after that), so the document identity
    -- cannot be the key. Ordering by this column is also how a reviewer works the queue
    -- oldest-first without depending on a timestamp's resolution.
    review_id          bigint         IDENTITY(1,1) NOT NULL,

    -- The document this row is about, in exactly the canonical '<algorithm>:<hash>' rendering
    -- document_registry stores and every other table joins on. Same width, same binary
    -- collation, and the FK is what makes "queued for a document nobody registered" impossible
    -- - a row naming a document the pipeline never ingested would send a reviewer looking for
    -- a file that does not exist.
    document_id        varchar(80)    COLLATE Latin1_General_BIN2 NOT NULL,

    -- Why the document is here, as the name of a Cbix.Core.Review.ReviewReason value rather
    -- than its ordinal. The name survives a value being inserted into the middle of the enum;
    -- an ordinal silently re-labels every historical row when that happens, and this table is
    -- the measurement the improvement flywheel runs on (5.7).
    --
    -- The CHECK is not a substitute for the application's enum - it is the guard for everything
    -- that writes here without going through it: a migration, a support script, a future
    -- service. Extending the reason set means extending this list, deliberately, which is the
    -- intended friction.
    --
    -- BIN2, for the reason document_registry.sql already records about its identity columns: under
    -- the usual case-insensitive database collation the CHECK below would accept 'triagelowconfidence'
    -- and 'TRIAGELOWCONFIDENCE' as the same value, so one reason would acquire several spellings and
    -- every GROUP BY over this column - which is the measurement the improvement flywheel runs on
    -- (5.7) - would split into buckets that mean the same thing. The binary collation is what makes
    -- the enum-name CHECK actually case-sensitive.
    reason             varchar(40)    COLLATE Latin1_General_BIN2 NOT NULL,

    -- What a reviewer reads first. Written by CBIX, never a verbatim model reply: where it
    -- quotes anything a model wrote (an undeclared field name, for instance), the quoting code
    -- sanitises control, format and surrogate characters first - see DocumentProfileParser.
    -- 1000 matches Cbix.Core.Review.ReviewQueueEntry.MaxDetailLength, which is enforced
    -- application-side so the bound is a decision made where the text is built rather than an
    -- error thrown where it is stored.
    --
    -- OPEN, and named here rather than left to be rediscovered: this column is a DESCRIPTION, not
    -- the model's reply. A refused triage reply is carried in run state and reaches a reviewer
    -- through the run, but it has no durable home in this schema - so once Sprint 03 makes the
    -- queue real, a reviewer opening a row from a previous process cannot see what the model
    -- actually said. Whether that verbatim text belongs here (a wide nullable column), in
    -- extraction_runs alongside the rest of the run's provenance, or nowhere at all is a Sprint 03
    -- shape decision, and design 8's "what produced this value" bar is the thing it has to answer.
    detail             nvarchar(1000) NOT NULL,

    -- Which agent's output produced this row: part of design 8's provenance record, and the
    -- column that answers "is one agent generating all our review work". Width matches the
    -- agent names the graph uses (node ids), with room for the model-qualified form Sprint 02
    -- may want.
    agent_name         nvarchar(120)  NOT NULL,

    -- The confidence the model reported, NULL when there was no parseable profile to take one
    -- from. Nullable rather than defaulted to zero, because "the model was not confident" and
    -- "the model said nothing usable" are different rows and a zero would merge them - and the
    -- distribution of this column against actual correction rates is precisely what design 11
    -- says the routing threshold must eventually be calibrated from.
    --
    -- decimal(4,3), matching field_provenance.confidence in design 6: three decimal places on
    -- the unit interval. A float would make "confidence = 0.7" a query that sometimes misses.
    reported_confidence decimal(4,3)  NULL,

    queued_utc         datetime2      NOT NULL,

    -- Sprint 03's HITL columns, present and unused (see the scope note above). claimed_by is
    -- what stops two reviewers working the same row; resolved_utc is what takes a row out of
    -- the queue without deleting it, because the trail of what was corrected IS the golden-set
    -- feed (5.7) and a deleted row is a correction nobody can learn from.
    claimed_by         nvarchar(100)  NULL,
    claimed_utc        datetime2      NULL,
    resolved_by        nvarchar(100)  NULL,
    resolved_utc       datetime2      NULL,

    CONSTRAINT pk_review_queue PRIMARY KEY (review_id),

    CONSTRAINT fk_review_queue_document FOREIGN KEY (document_id)
        REFERENCES dbo.document_registry (document_id),

    -- The names of Cbix.Core.Review.ReviewReason's members. Sprint 02 adds the validator's
    -- reasons (failed grounding, referential integrity, incomplete matrix) and the normaliser's
    -- UNMAPPED; each is an extension of this list made on purpose.
    CONSTRAINT ck_review_queue_reason
        CHECK (reason IN ('TriageLowConfidence', 'TriageReplyRefused')),

    -- The scale already bounds the value to +/-9.999, which is not the same as the unit
    -- interval. A confidence outside [0,1] is a reply the parser would have refused, so a row
    -- carrying one means something wrote here without going through the application - exactly
    -- the case the CHECK constraints in this schema exist for.
    CONSTRAINT ck_review_queue_confidence
        CHECK (reported_confidence IS NULL OR (reported_confidence >= 0 AND reported_confidence <= 1)),

    -- A row cannot be resolved before it was queued, or claimed before it was queued. Ordering
    -- checks rather than triggers, for the same reason the registry uses CHECKs: they cannot be
    -- disabled by anyone who can ALTER TABLE.
    CONSTRAINT ck_review_queue_claim_order
        CHECK (claimed_utc IS NULL OR claimed_utc >= queued_utc),
    CONSTRAINT ck_review_queue_resolution_order
        CHECK (resolved_utc IS NULL OR resolved_utc >= queued_utc),

    -- The two halves of each pair travel together. A resolved_utc with no resolved_by is a
    -- correction with no author, which is precisely the thing an audit-grade pipeline may not
    -- record (design 8 requires the reviewer be answerable in one query).
    --
    -- Written as explicit AND/OR predicates rather than as `(a IS NULL) = (b IS NULL)`. That
    -- shorter form is PostgreSQL: T-SQL has no boolean VALUE, only boolean predicates, so `IS NULL`
    -- cannot appear as an operand of `=`. It is not a warning or a runtime error - the batch fails
    -- to PARSE, which takes the entire CREATE TABLE with it: the foreign key, every other CHECK,
    -- the whole table. Nothing in CI parses .sql, so it would have surfaced in Sprint 03 as a
    -- migration that did not run.
    CONSTRAINT ck_review_queue_claim_pairing
        CHECK ((claimed_by IS NULL     AND claimed_utc IS NULL)
            OR (claimed_by IS NOT NULL AND claimed_utc IS NOT NULL)),
    CONSTRAINT ck_review_queue_resolution_pairing
        CHECK ((resolved_by IS NULL     AND resolved_utc IS NULL)
            OR (resolved_by IS NOT NULL AND resolved_utc IS NOT NULL))
);

/*
    Idempotency under resume, which is a correctness requirement rather than tidiness
    ---------------------------------------------------------------------------------
    A resumed run replays the superstep that queued the document (5.2 checkpoints per
    superstep), so the same row will be offered more than once for reasons that are nobody's
    bug. Without a guard the queue grows by one row per worker restart, and a reviewer's
    backlog becomes a function of infrastructure stability.

    The filtered unique index below is the guard: at most one OPEN row per (document, reason).
    It is filtered on resolved_utc IS NULL deliberately - a document legitimately returns to
    the queue after an earlier visit was resolved (a new version, a later gate), and a
    non-filtered unique index would make that second visit an insert failure.

    IReviewQueue.EnqueueAsync's contract states this as the implementation's problem rather
    than the caller's, which is why the constraint lives here rather than as a lookup-then-
    insert in application code: a check-then-act would race two concurrent runs of the same
    document, and the index does not.

    LIVE DDL, not commented out. The permission statements below are commented because they
    name principals that do not exist until the Sprint 03 deployment creates them; an index
    needs nothing but the table it is on, so commenting it would leave the correctness
    property it enforces described rather than enforced - and it is the one property here that
    a restart, rather than a bug, is enough to violate.
*/
CREATE UNIQUE INDEX ux_review_queue_open_per_document_reason
    ON dbo.review_queue (document_id, reason)
    WHERE resolved_utc IS NULL;

/*
    The index a reviewer's queue view actually reads: open rows, oldest first.
*/
CREATE INDEX ix_review_queue_open_by_age
    ON dbo.review_queue (queued_utc)
    INCLUDE (document_id, reason, agent_name, reported_confidence)
    WHERE resolved_utc IS NULL;

/*
    Permissions, written here next to the table rather than left implied
    --------------------------------------------------------------------
    Principals are created with the rest of the deployment in Sprint 03, so the statements
    are commented; the intent is not.

    The pipeline WRITES rows and never resolves them. That separation is the point: a service
    principal that could mark a row resolved could make a document that needed a human look
    like one that had had one, and nothing in the published data would say otherwise.

        -- GRANT SELECT, INSERT ON dbo.review_queue TO [cbix_pipeline];
        -- DENY  UPDATE, DELETE ON dbo.review_queue TO [cbix_pipeline];

    The review UI claims and resolves, and may not insert: a row that appeared without a
    pipeline decision behind it would be a review with no subject.

        -- GRANT SELECT, UPDATE ON dbo.review_queue TO [cbix_review_ui];
        -- DENY  INSERT, DELETE ON dbo.review_queue TO [cbix_review_ui];

    Nobody deletes. A resolved row is the golden-set feed (5.7) and the audit trail of what a
    human changed; removing one removes the only record that the pipeline was ever wrong in
    that particular way.

        -- GRANT SELECT ON dbo.review_queue TO [cbix_reader];

    DENY rather than simply withholding GRANT, for the reason document_registry.sql records:
    DENY survives a principal later joining a role that grants the right more broadly.
*/
