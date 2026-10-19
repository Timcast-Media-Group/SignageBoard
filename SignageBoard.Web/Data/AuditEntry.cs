namespace SignageBoard.Web.Data
{
    public class AuditEntry
    {
        public string Id { get; set; }
        public string UserUpn { get; set; }
        public string Action { get; set; }
        public string? NoteId { get; set; }
        public string Before { get; set; }
        public string After { get; set; }
    }
}
