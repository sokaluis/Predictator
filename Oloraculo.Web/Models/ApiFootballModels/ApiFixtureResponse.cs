namespace Oloraculo.Web.Models.ApiFootballModels
{
    public class ApiFixtureResponse
    {
        public System.Text.Json.JsonElement Errors { get; set; }
        public List<ApiFixtureRow> Response { get; set; } = [];
    }
}
