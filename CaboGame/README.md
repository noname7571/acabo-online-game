# Cabo Online Game Backend

This is the backend for the real-time multiplayer Cabo card game, built with ASP.NET Core and WebSockets.

## Structure
- **Controllers/**: WebSocket endpoint
- **Game/**: Game and lobby management logic
- **Services/**: WebSocket helper service

## Running the Backend
1. Ensure you have [.NET 8 SDK](https://dotnet.microsoft.com/download) installed.
2. Navigate to `CaboGame` directory.
3. Run:
   ```sh
   dotnet run
   ```
4. The WebSocket endpoint will be available at `ws://localhost:5000/ws`.

## Next Steps
- Implement message handling and game logic in `WebSocketController`.
- Build the frontend in `/public`.
