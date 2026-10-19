namespace SignageBoard.Web.Data
{
    public class Note
    {
        public string Id { get; set; }
        public string Body { get; set; }
        public int Priority { get; set; }
        public int SortOrder { get; set; }
        public DateTime? ExpiresAt { get; set; }
        public string CreatedBy { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public NoteStatus Status { get; set; } = NoteStatus.Draft;
    }

    public enum NoteStatus
    {
        Draft = 1,
        Unpublished = 2,
        Published = 3
    }
}
