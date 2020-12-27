using Newtonsoft.Json;

namespace ErtisAuth.WebAPI.Models.Request.Applications
{
	public class CreateApplicationFormModel
	{
		#region Properties

		[JsonProperty("name")]
		public string Name { get; set; }

		[JsonProperty("secret")]
		public string Secret { get; set; }
		
		[JsonProperty("role")]
		public string Role { get; set; }
		
		#endregion
	}
}