using newApi.Scripts;

namespace newApi.Scripts
{
    public class Program
    {
        public static async Task Main(string[] args)
        {
            try
            {
                await CreateLogTypeTable.CreateTableAndDataAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Error: {ex.Message}");
                Environment.Exit(1);
            }
        }
    }
}
