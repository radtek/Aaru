using System.ComponentModel.DataAnnotations;

namespace DiscImageChef.Database.Models
{
    public class Partition
    {
        [Key]
        public int Id { get;              set; }
        public string Name         { get; set; }
        public bool   Synchronized { get; set; }
    }
}