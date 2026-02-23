document.addEventListener('DOMContentLoaded', () => {
    connectWebSocket(handleWSMessage);
    renderLobbyForm();
});

let currentLobbyId = null;
let currentPlayerId = null;

function renderLobbyForm() {
    document.getElementById('lobby-section').innerHTML =
        '<input type="text" id="playerName" placeholder="Enter your name">' +
        '<button id="createLobbyBtn">Create Lobby</button>' +
        '<input type="text" id="lobbyId" placeholder="Lobby ID">' +
        '<button id="joinLobbyBtn">Join Lobby</button>';

    document.getElementById('createLobbyBtn').onclick = () => {
        const playerName = document.getElementById('playerName').value;
        sendWSMessage({ type: 'create_lobby', playerName });
    };
    document.getElementById('joinLobbyBtn').onclick = () => {
        const playerName = document.getElementById('playerName').value;
        const lobbyId = document.getElementById('lobbyId').value;
        sendWSMessage({ type: 'join_lobby', lobbyId, playerName });
    };
}

function handleWSMessage(msg) {
    if (msg.type === 'lobby_update') {
        if (msg.selfPlayerId) currentPlayerId = msg.selfPlayerId;
        currentLobbyId = msg.lobbyId;
        document.getElementById('lobby-section').innerHTML =
            `<h2>Lobby: ${msg.lobbyId}</h2>` +
            '<ul>' + msg.players.map(p => `<li>${p.name}</li>`).join('') + '</ul>' +
            '<button id="startGameBtn">Start Game</button>' +
            '<button id="leaveLobbyBtn">Back</button>';

        document.getElementById('startGameBtn').onclick = () => {
            sendWSMessage({ type: 'start_game', lobbyId: currentLobbyId });
        };
        document.getElementById('leaveLobbyBtn').onclick = () => {
            // simply re-render the create/join form so user can change name or create a new lobby
            renderLobbyForm();
        };
    } else if (msg.type === 'game_start') {
        if (msg.selfPlayerId) currentPlayerId = msg.selfPlayerId;
        document.getElementById('lobby-section').style.display = 'none';
        // reset pending draw
        window._pendingDraw = null;
        showGame(msg.gameState);
    } else if (msg.type === 'game_update') {
        if (msg.selfPlayerId) currentPlayerId = msg.selfPlayerId;
        showGame(msg.gameState, msg.result);
    } else if (msg.type === 'peek_result') {
        // show a temporary alert with the peeked card
        alert(`Peeked card [${msg.index}]: ${msg.card}`);
    } else if (msg.type === 'ability_peek') {
        alert(`Ability peek: player ${msg.targetPlayer} card[${msg.index}] = ${msg.card}`);
    } else if (msg.type === 'ability_spy') {
        alert(`Ability spy: player ${msg.targetPlayer} card[${msg.index}] = ${msg.card}`);
    } else if (msg.type === 'draw_offer') {
        // store pending draw for the local player and re-render
        window._pendingDraw = msg.card;
        showGame(msg.gameState || (window._lastGameState || {}));
    } else if (msg.type === 'error') {
        alert(msg.message);
    }
}
