namespace SignageBoard.Web.Data
{
    public class Note
    {
        public string Id { get; set; }
        public string Body { get; set; }
        public NotePriority Priority { get; set; }
        public int SortOrder { get; set; }
        public DateTime? ExpiresAt { get; set; }
        public string CreatedBy { get; set; }
        public string? CreatedByName { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public NoteStatus Status { get; set; } = NoteStatus.Draft;
    }

    public enum NoteStatus
    {
        Draft = 0,
        Published = 1,
        Archived = 2
    }

    public enum NotePriority
    {
        Normal = 0,
        Important = 1,
        Urgent = 2
    }
}
