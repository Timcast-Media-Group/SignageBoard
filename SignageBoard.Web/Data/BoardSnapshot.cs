namespace SignageBoard.Web.Data
{
    public sealed record BoardSnapshot(long Version, bool SingleNoteMode, IReadOnlyList<Note> Notes);
}
