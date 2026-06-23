using Microsoft.AspNetCore.Mvc;

namespace I3X4Kusto.Controllers
{
    [ApiController]
    [Route("v1/info")]
    public sealed class InfoController : ControllerBase
    {
        // Health-check / capabilities endpoint. Does not require authentication
        // and does not touch the backing store.
        [HttpGet]
        public ActionResult<SuccessResponse<ServerInfo>> GetInfo()
        {
            var info = new ServerInfo(
                SpecVersion: "1.0",
                Capabilities: new ServerCapabilities(
                    new QueryCapabilities(History: true),
                    new UpdateCapabilities(Current: false, History: false),
                    new SubscribeCapabilities(Stream: false)),
                ServerVersion: "1.0.0",
                ServerName: "I3X4Kusto");

            return Ok(new SuccessResponse<ServerInfo>(true, info));
        }
    }
}
