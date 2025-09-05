namespace Files.App.Communication.Models
{
	public sealed class ItemDto
	{
		public string Path { get; set; } = string.Empty;

		public string Name { get; set; } = string.Empty;

		public bool IsDirectory { get; set; }

		public long SizeBytes { get; set; }

		public DateTimeOffset DateModified { get; set; }

		public DateTimeOffset DateCreated { get; set; }

		public string? MimeType { get; set; }

		public bool Exists { get; set; }
	}
}