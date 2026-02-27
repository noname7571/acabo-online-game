# Cabo Online Game

A simple multiplayer implementation of the popular card game **Cabo** using ASP.NET Core WebSockets for the server and vanilla HTML/CSS/JS for the frontend.

---

## Features

- Real‑time multiplayer lobby with up to 4 players.
- Turn timer optionally enabled per lobby.
- Draw from deck or swap with discard pile.
- Initial peeks at your own cards (2 per player).
- Special cards pending implementation:
  - **7–8**: *Peek* – view another player's card privately.
  - **9–10**: *Spy* – identical to peek (synonym).
  - **11–12**: *Swap* – swap one of your cards with another player's card.
- Cabo mechanic with countdown after a player calls Cabo.
- Score totals revealed at end of game with "Play Again" button.
- Responsive table layout suitable for desktop and mobile.

## Game Rules Overview

Players start with a hand of four hidden cards. On your turn you may:
1. Draw the top card of the deck and either:
   - Discard it immediately to end your turn,
   - Swap it with one of your hand cards,
   - Use it as part of an ability (peek/spy/swap) – when those are implemented.
2. Or take the top card from the discard pile and swap it with a card in your hand
   (the replaced card goes immediately to the discard pile).

At any time on your turn you may call **Cabo**. A countdown begins and after every other
player has completed a turn the hands are revealed and totals computed; lowest score wins.

Card values are numeric 0‑13, but certain ranges will act as abilities in future updates:
- 0–6: standard point cards
- **7–8 (peek)**: when drawn you may secretly view one card from another player's hand
- **9–10 (spy)**: identical to peek, just alternate naming
- **11–12 (swap)**: allows swapping one of your hand cards with another player's card
- 13: a special card (if used) or high point value

## Repository Structure

```
acabo-online-game.sln
CaboGame/          # ASP.NET Core backend project
  Controllers/
    WebSocketController.cs            # main controller (split into partials)
    WebSocketController.Lobby.cs      # lobby and game-start logic
    WebSocketController.Game.cs       # in‑game action handlers
  Game/                               # shared game state and models
    GameManager.cs
    LobbyManager.cs
    Models/                            # Player, etc.
public/               # frontend assets
  index.html
  css/style.css
  js/
    ws.js            # common WebSocket wrapper
    lobby.js         # lobby UI logic
    game.js          # in‑game UI logic
```

## Getting Started

### Prerequisites
- [.NET 9 SDK](https://dotnet.microsoft.com/download)
- A modern web browser

### Running the server

```bash
cd CaboGame
dotnet run
```

The backend listens on the default Kestrel port (usually `http://localhost:5000`).
It exposes a WebSocket endpoint at `/ws` used by the frontend.

### Serving the frontend

You can simply open `public/index.html` in your browser or serve it with a local
HTTP server (e.g. `npx serve public`). The frontend connects to `ws://localhost:5000/ws`.

## Playing the Game

1. Open the frontend in your browser and enter a name.
2. Create a lobby or join an existing one (players share the lobby ID).
3. Once at least 2 players are present, click **Start Game**.
4. Use the buttons in the centre to draw or take from the pile; click cards to swap.
5. When you draw, you will see the card and can choose to discard or swap.
6. Call **Cabo** when you think you have the lowest total.
7. After Cabo countdown expires, the hands reveal and totals appear; you can then
   hit **Play Again** to restart.

Special abilities (7‑12) are not yet functional but their values are reserved and the
UI will allow selecting targets when they are implemented in a future update.

## Development Notes

- The WebSocket controller was refactored into partial classes for clarity.
- Game state is kept in memory; no persistence.
- Client maintains a small amount of transient state (`_pendingDraw`, etc.) to
  support draw offers and interactions.
- The frontend uses simple DOM manipulation and is intentionally dependency‑free.

## Contributing

Feel free to open issues or submit pull requests. Future enhancements might include:

- Implement the 7–12 card abilities.
- Add AI players or offline mode.
- Improve UX with animations or mobile touch controls.
- Persistent scoring and lobby history.

---

Enjoy playing Cabo online!
