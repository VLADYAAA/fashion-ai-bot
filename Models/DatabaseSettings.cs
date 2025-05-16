using System.ComponentModel.DataAnnotations;
namespace FashionBot.Models
{
public class DatabaseSettings
{
    [Required(ErrorMessage = "ConnectionString is required")]
    public string ConnectionString { get; set; } = string.Empty;
}
}