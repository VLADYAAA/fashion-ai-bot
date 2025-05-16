namespace FashionBot.Models
{
    public class FashionRequest
    {
        public Guid Id { get; set; }
        public long UserId { get; set; }
        public FashionRequestType RequestType { get; set; }
        public List<string> Images { get; set; } = new();
        public string Prompt { get; set; } = string.Empty;
        public string ResultUrl { get; set; } = string.Empty;
        public FashionRequestStatus Status { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? ProcessedAt { get; set; }
    }

    public enum FashionRequestType
    {
        CombineOutfit,
        MatchOutfit
    }

    public enum FashionRequestStatus
    {
        Queued,
        Processing,
        Completed,
        Failed
    }


}