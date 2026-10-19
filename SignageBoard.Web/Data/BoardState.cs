namespace SignageBoard.Web.Data
{
    public class BoardState
    {
        public bool IsLocked { get; set; }
        public string? LockedBy { get; set; }
        public DateTime? LockedAt { get; set; }
        public bool ScheduleEnabled { get; set; } = false;
    }
}
