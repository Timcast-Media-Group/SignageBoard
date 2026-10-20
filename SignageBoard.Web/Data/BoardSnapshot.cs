namespace SignageBoard.Web.Data
{
    public sealed record BoardSnapshot(long Version, int FontScale, bool SingleNoteMode, IReadOnlyList<Note> Notes);
}
