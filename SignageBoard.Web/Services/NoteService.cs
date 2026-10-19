using Microsoft.Extensions.Options;
using SignageBoard.Web.Data;

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
    }
}
