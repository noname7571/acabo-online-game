using System.Net.WebSockets;
using System.Threading.Tasks;
using System.Text.Json;
using System.Text;

namespace CaboGame.Services
{
    public class WebSocketService
    {
        public async Task SendAsync(WebSocket socket, object message)
        {
            var json = JsonSerializer.Serialize(message);
            var buffer = Encoding.UTF8.GetBytes(json);
            await socket.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Text, true, CancellationToken.None);
        }
    }
}
