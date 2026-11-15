using Newtonsoft.Json.Linq;
using PawnMind.Bridge;
using PawnMind.Core;

namespace PawnMind.Tests
{
    internal static class ClienteleServiceTests
    {
        private static JObject Json(string text)
        {
            return JObject.Parse(text);
        }

        [Test]
        public static void Json_Readable_Exposes_Types_Predictions_And_Counts()
        {
            var port = new FakeClientelePort { Raw = IntelTestData.Clientele() };
            var service = new ClienteleService(port);

            JObject json = Json(service.Json());

            Assert.Equal(true, (bool)json["ok"], "read is ok");
            Assert.Equal(true, (bool)json["readable"], "readable");
            Assert.Equal(3, (int)json["day"], "day");
            Assert.Equal("LUXURY_ITEM", (string)json["showcase_types"][0]["id"], "type id");
            Assert.Equal("Luxury", (string)json["showcase_types"][0]["name"], "type name");
            Assert.Equal(10, (int)json["discerning_shopper"]["chance"], "discerning chance");
            Assert.Equal(0, (int)json["discerning_shopper"]["cooldown_openings"], "cooldown");
            Assert.Equal("ulDiscerningShopper", (string)json["predictions"][0]["client_id"], "client id");
            Assert.Equal("Discerning", (string)json["predictions"][0]["client_name"], "client name");
            Assert.Equal(10, (int)json["predictions"][0]["chance"], "chance");
            Assert.Equal("vase", (string)json["predictions"][0]["item_id"], "item id");
            Assert.Equal("Vase", (string)json["predictions"][0]["item_name"], "item name");
            Assert.Equal(2, (int)json["normal_customer_count"], "normal customers");
            Assert.Equal(1, (int)json["prediction_count"], "prediction count");
            Assert.Equal(1, port.Calls, "one port read");
        }

        [Test]
        public static void Json_Unreadable_Is_Fail_Closed()
        {
            var port = new FakeClientelePort();
            port.Raw = new ClienteleRaw { Readable = false, Error = "not_in_game" };

            var service = new ClienteleService(port);
            JObject json = Json(service.Json());

            Assert.Equal(true, (bool)json["ok"], "read op stays ok");
            Assert.Equal(false, (bool)json["readable"], "unreadable");
            Assert.Equal("not_in_game", (string)json["error"], "adapter error");
            Assert.Equal(0, ((JArray)json["showcase_types"]).Count, "no types");
            Assert.Equal(0, ((JArray)json["predictions"]).Count, "no predictions");
        }

        [Test]
        public static void Json_Tolerates_A_Throwing_Port()
        {
            var port = new FakeClientelePort { Throw = true };
            var service = new ClienteleService(port);

            JObject json = Json(service.Json());

            Assert.Equal(false, (bool)json["readable"], "unreadable");
            Assert.Equal(ClienteleErrors.Unavailable, (string)json["error"], "stable error");
        }

        [Test]
        public static void Json_Without_A_Port_Is_Unreadable()
        {
            var service = new ClienteleService(null);
            JObject json = Json(service.Json());

            Assert.Equal(false, (bool)json["readable"], "no port");
            Assert.Equal(ClienteleErrors.Unavailable, (string)json["error"], "stable error");
        }

        [Test]
        public static void Json_Exposes_Warnings()
        {
            ClienteleRaw raw = IntelTestData.Clientele();
            raw.Warnings.Add("types_unavailable");

            var service = new ClienteleService(new FakeClientelePort { Raw = raw });
            JObject json = Json(service.Json());

            Assert.Equal("types_unavailable", (string)json["warnings"][0], "warning exposed");
        }
    }
}
