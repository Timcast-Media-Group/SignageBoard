namespace SignageBoard.Web.Data
{
    public sealed record BoardSnapshot(long Version, IReadOnlyList<Note> Notes);
}
