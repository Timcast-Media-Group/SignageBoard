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
            return await QueryActiveAsync(conn, null);
        }

        public async Task<IReadOnlyList<Note>> GetAllAsync()
        {
            using var conn = _db.Create();
            var notes = await conn.QueryAsync<Note>("""
                SELECT * FROM Notes
                WHERE Status <> @archived
                ORDER BY Status DESC, Priority = @urgent DESC, CreatedAt DESC, SortOrder DESC
                """, new { archived = NoteStatus.Archived, urgent = NotePriority.Urgent });
            return notes.AsList();
        }

        public async Task<BoardSnapshot> GetBoardAsync()
        {
            using var conn = _db.Create();
            using var tx = conn.BeginTransaction(deferred: true);

            var state = await conn.QuerySingleAsync<(long Version, long FontScale, long SingleNoteMode)>(
                "SELECT Version, FontScale, SingleNoteMode FROM BoardState WHERE Id = 1", transaction: tx);
            var notes = await QueryActiveAsync(conn, tx);

            tx.Commit();
            return new BoardSnapshot(state.Version, (int)state.FontScale, state.SingleNoteMode != 0, notes);
        }

        public async Task<int> GetFontScaleAsync()
        {
            using var conn = _db.Create();
            return (int)await conn.ExecuteScalarAsync<long>(
                "SELECT FontScale FROM BoardState WHERE Id = 1");
        }

        public async Task<int> SetFontScaleAsync(int percent)
        {
            var value = (int)(Math.Round(Math.Clamp(percent, 50, 200) / 5.0, MidpointRounding.AwayFromZero) * 5);

            using var conn = _db.Create();
            using var tx = conn.BeginTransaction(deferred: false);

            await conn.ExecuteAsync("""
                UPDATE BoardState
                SET FontScale = @value, Version = Version + 1
                WHERE Id = 1 AND FontScale <> @value
                """, new { value }, tx);

            tx.Commit();
            return value;
        }

        public async Task<bool> GetSingleNoteModeAsync()
        {
            using var conn = _db.Create();
            return await conn.ExecuteScalarAsync<long>(
                "SELECT SingleNoteMode FROM BoardState WHERE Id = 1") != 0;
        }

        public async Task SetSingleNoteModeAsync(bool enabled)
        {
            using var conn = _db.Create();
            using var tx = conn.BeginTransaction(deferred: false);

            await conn.ExecuteAsync("""
                UPDATE BoardState
                SET SingleNoteMode = @mode, Version = Version + 1
                WHERE Id = 1 AND SingleNoteMode <> @mode
                """, new { mode = enabled ? 1 : 0 }, tx);

            tx.Commit();
        }

        public async Task DeleteNoteAsync(string noteId)
        {
            using var conn = _db.Create();
            using var tx = conn.BeginTransaction(deferred: false);

            var note = await FindLiveNoteAsync(conn, tx, noteId);

            await conn.ExecuteAsync(
                "UPDATE Notes SET Status = @archived, UpdatedAt = @now WHERE Id = @noteId",
                new { archived = NoteStatus.Archived, now = DateTime.UtcNow, noteId }, tx);

            if (note.Status == NoteStatus.Published)
                await conn.ExecuteAsync(
                    "UPDATE BoardState SET Version = Version + 1 WHERE Id = 1", transaction: tx);

            tx.Commit();
        }

        public async Task<Note> CreateNoteAsync(CreateNoteDto dto, string createdBy, string? createdByName = null)
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

            if (dto.Publish)
                await EnsureBoardHasRoomAsync(conn, tx, now);

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
                CreatedByName = string.IsNullOrWhiteSpace(createdByName) ? null : createdByName.Trim(),
                CreatedAt = now,
                UpdatedAt = now,
                Status = dto.Publish ? NoteStatus.Published : NoteStatus.Draft
            };

            await conn.ExecuteAsync("""
                INSERT INTO Notes (Id, Body, Priority, SortOrder, ExpiresAt, CreatedBy, CreatedByName, CreatedAt, UpdatedAt, Status)
                VALUES (@Id, @Body, @Priority, @SortOrder, @ExpiresAt, @CreatedBy, @CreatedByName, @CreatedAt, @UpdatedAt, @Status)
                """, newNote, tx);

            await conn.ExecuteAsync(
                "UPDATE BoardState SET Version = Version + 1 WHERE Id = 1", transaction: tx);

            tx.Commit();
            return newNote;
        }

        public Task<Note> PublishNoteAsync(string noteId) =>
            SetStatusAsync(noteId, NoteStatus.Published);

        public Task<Note> UnpublishNoteAsync(string noteId) =>
            SetStatusAsync(noteId, NoteStatus.Draft);

        private async Task<Note> SetStatusAsync(string noteId, NoteStatus status)
        {
            using var conn = _db.Create();
            using var tx = conn.BeginTransaction(deferred: false);

            var note = await FindLiveNoteAsync(conn, tx, noteId);

            if (note.Status == status)
                return note;

            var now = DateTime.UtcNow;

            if (status == NoteStatus.Published)
            {
                if (note.ExpiresAt is { } exp && exp <= now)
                    throw new ValidationException("Can't publish an expired note.");

                await EnsureBoardHasRoomAsync(conn, tx, now);
            }

            note.Status = status;
            note.UpdatedAt = now;

            await conn.ExecuteAsync(
                "UPDATE Notes SET Status = @Status, UpdatedAt = @UpdatedAt WHERE Id = @Id",
                note, tx);

            await conn.ExecuteAsync(
                "UPDATE BoardState SET Version = Version + 1 WHERE Id = 1", transaction: tx);

            tx.Commit();
            return note;
        }

        private static async Task<IReadOnlyList<Note>> QueryActiveAsync(SqliteConnection conn, SqliteTransaction? tx)
        {
            var notes = await conn.QueryAsync<Note>("""
                SELECT * FROM Notes
                WHERE Status = @published AND (ExpiresAt IS NULL OR ExpiresAt > @now)
                ORDER BY Priority = @urgent DESC, CreatedAt DESC, SortOrder DESC
                """, new { published = NoteStatus.Published, urgent = NotePriority.Urgent, now = DateTime.UtcNow }, tx);
            return notes.AsList();
        }

        private static async Task<Note> FindLiveNoteAsync(SqliteConnection conn, SqliteTransaction tx, string noteId) =>
            await conn.QueryFirstOrDefaultAsync<Note>(
                "SELECT * FROM Notes WHERE Id = @noteId AND Status <> @archived",
                new { noteId, archived = NoteStatus.Archived }, tx)
            ?? throw new KeyNotFoundException($"Note {noteId} not found.");

        private async Task EnsureBoardHasRoomAsync(SqliteConnection conn, SqliteTransaction tx, DateTime now)
        {
            var publishedCount = await conn.ExecuteScalarAsync<int>("""
                SELECT COUNT(*) FROM Notes
                WHERE Status = @published AND (ExpiresAt IS NULL OR ExpiresAt > @now)
                """, new { published = NoteStatus.Published, now }, tx);

            if (publishedCount >= _opts.Value.MaxNotes)
                throw new ValidationException($"Board is full ({_opts.Value.MaxNotes} notes max).");
        }
    }
}
