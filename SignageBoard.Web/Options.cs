using System.ComponentModel.DataAnnotations;

namespace SignageBoard.Web
{
    public sealed class BoardOptions
    {
        public const string Section = "Board";

        [Required]
        public string EditorGroupId { get; init; } = "";

        [Required]
        public DisplayOptions Display { get; init; } = new();

        [Range(1, 2000)]
        public int MaxNoteLength { get; init; } = 280;

        [Range(1, 100)]
        public int MaxNotes { get; init; } = 12;
    }

    public sealed class DisplayOptions
    {
        public string[] AllowedIps { get; init; } = [];

        [Required]
        public string KeyHash { get; init; } = "";

        [Range(2, 300)]
        public int PollSeconds { get; init; } = 10;
    }
}
