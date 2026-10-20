using System.ComponentModel.DataAnnotations;

namespace SignageBoard.Web.Data
{
    public sealed class CreateNoteDto
    {
        [Required(AllowEmptyStrings = false, ErrorMessage = "Note can't be empty.")]
        [StringLength(280, ErrorMessage = "Keep it under {1} characters.")]
        public string Body { get; set; } = "";

        public NotePriority Priority { get; set; } = NotePriority.Normal;

        public DateTimeOffset? ExpiresAt { get; set; }

        public bool Publish { get; set; }
    }
}
