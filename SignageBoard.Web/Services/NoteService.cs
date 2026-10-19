using Microsoft.Extensions.Options;
using SignageBoard.Web.Data;
using System.ComponentModel.DataAnnotations;

namespace SignageBoard.Web.Services
{
    public class NoteService
    {
        private IOptions<BoardOptions> _opts { get; init; }
        private SqliteConnectionFactory _db { get; init; }

        public NoteService(IOptions<BoardOptions> _opts, SqliteConnectionFactory _db)
        {
            this._opts = _opts;
            this._db = _db;
        }

        public async Task<IReadOnlyList<Note>> GetActiveAsync()
        {
            using var conn = _db.Create();
            var notes = await conn.QueryAsync<Note>(
                "SELECT * FROM Notes WHERE ExpiresAt IS NULL OR ExpiresAt > @now ORDER BY SortOrder",
                new { now = DateTime.UtcNow });
            return notes.AsList();
        }

        public async Task<Note> CreateNoteAsync(CreateNoteDto dto, string createdBy)
        {
            var body = dto.Body?.Trim() ?? "";
            if (body.Length == 0 || body.Length > _opts.Value.MaxNoteLength)
                throw new ValidationException("Invalid note length.");

            DateTime? expiresUtc = dto.ExpiresAt?.UtcDateTime;
            if (expiresUtc is { } exp && exp <= DateTime.UtcNow)
                throw new ValidationException("Expiry must be in the future.");

            if (string.IsNullOrWhiteSpace(createdBy))
                throw new ValidationException("Missing author.");

            using var conn = _db.Create();
            using var tx = conn.BeginTransaction(deferred: false);

            var now = DateTime.UtcNow;

            var activeCount = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM Notes WHERE ExpiresAt IS NULL OR ExpiresAt > @now",
                new { now }, tx);
            if (activeCount >= _opts.Value.MaxNotes)
                throw new ValidationException($"Board is full ({_opts.Value.MaxNotes} notes max).");

            var nextSort = await conn.ExecuteScalarAsync<int>(
                "SELECT COALESCE(MAX(SortOrder), -1) + 1 FROM Notes", transaction: tx);

            var newNote = new Note
            {
                Id = Guid.NewGuid().ToString(),
                Body = body,
                Priority = dto.Priority,
                SortOrder = nextSort,
                ExpiresAt = expiresUtc,
                CreatedBy = createdBy,
                CreatedAt = now,
                UpdatedAt = now
            };

            await conn.ExecuteAsync("""
        INSERT INTO Notes (Id, Body, Priority, SortOrder, ExpiresAt, CreatedBy, CreatedAt, UpdatedAt)
        VALUES (@Id, @Body, @Priority, @SortOrder, @ExpiresAt, @CreatedBy, @CreatedAt, @UpdatedAt)
        """, newNote, tx);

            await conn.ExecuteAsync(
                "UPDATE BoardState SET Version = Version + 1 WHERE Id = 1", transaction: tx);

            tx.Commit();
            return newNote;
        }
    }
}
