// Table-style game UI

// helper functions used by showGame
function updateTurnInfo(gameState) {
    const turnDiv = document.getElementById('turn-info');
    if (!turnDiv) return;
    const current = gameState.players && gameState.players[gameState.currentTurn] ? gameState.players[gameState.currentTurn].id : null;
    let text = '';
    if (current) {
        text = (current === window.currentPlayerId) ? 'Your turn' : `Player ${gameState.currentTurn + 1}'s turn`;
    }
    turnDiv.textContent = text;
    startTurnTimer(current === window.currentPlayerId);
}

function startTurnTimer(isMine) {
    clearInterval(window._turnTimer);
    window._turnCountdown = 15;
    const turnDiv = document.getElementById('turn-info');
    if (!turnDiv) return;
    window._turnTimer = setInterval(() => {
        window._turnCountdown -= 1;
        if (window._turnCountdown <= 0) {
            clearInterval(window._turnTimer);
            if (isMine) {
                sendWSMessage({ type: 'player_action', lobbyId: window._lastGameState.lobbyId, actionType: 'skip_turn' });
                if (turnDiv) turnDiv.textContent = 'Skipped due to timeout';
            }
        } else {
            turnDiv.textContent = (isMine ? 'Your' : 'Opponent') + ` turn - ${window._turnCountdown}s`;
        }
    }, 1000);
}

function renderPairUI() {
    const log = document.getElementById('game-log');
    if (!log) return;
    log.innerHTML = '';
    const msg = document.createElement('div'); msg.textContent = 'Select 2-4 cards, then confirm';
    log.appendChild(msg);
    const confirm = document.createElement('button');
    confirm.className = 'card-btn';
    confirm.textContent = 'Confirm';
    confirm.disabled = true;
    confirm.onclick = () => {
        sendWSMessage({ type: 'player_action', lobbyId: window._lastGameState.lobbyId, actionType: 'resolve_draw', payload: { action: 'pair_claim', indices: window._pairSelected } });
        window._pairClaimMode = false;
        window._pairSelected = [];
        log.textContent = '';
    };
    log.appendChild(confirm);
    const cancel = document.createElement('button');
    cancel.className = 'card-btn';
    cancel.textContent = 'Cancel';
    cancel.onclick = () => { window._pairClaimMode = false; window._pairSelected = []; log.textContent = ''; };
    log.appendChild(cancel);
    window._pairConfirmBtn = confirm;
}

// helper to flash temporary reveal
function revealTemp(playerId, idx) {
    window._tempReveals = window._tempReveals || {};
    if (!window._tempReveals[playerId]) window._tempReveals[playerId] = {};
    window._tempReveals[playerId][idx] = true;
    // remove after 2 seconds
    setTimeout(() => {
        if (window._tempReveals && window._tempReveals[playerId]) {
            delete window._tempReveals[playerId][idx];
        }
        showGame(window._lastGameState || {});
    }, 2000);
}


function showGame(gameState, result) {
    console.log('showGame called', gameState);
    if (gameState.players) {
        gameState.players.forEach((p,i)=>console.log('player',i,'hand',p.hand));
    }
    const gameSection = document.getElementById('game-section');
    gameSection.style.display = 'block';
    window._lastGameState = gameState || window._lastGameState;
    gameState = gameState || window._lastGameState || {};

    // clear seats
    ['player-top','player-left','player-right','player-bottom'].forEach(id => {
        const el = document.getElementById(id);
        if (el) el.innerHTML = '';
    });
    // update turn info and start countdown if provided
    updateTurnInfo(gameState);


    // helper: seating layout depending on player count
    const seatsForCount = (n) => {
        if (n === 2) return ['bottom','top'];
        if (n === 3) return ['bottom','left','top'];
        return ['bottom','left','top','right'];
    };

    let players = gameState.players || [];
    if (!players || players.length === 0) {
        // no data yet, show two placeholder hands
        players = [
            { id: 'placeholder1', hand: ['?','?','?','?'], revealed: [false,false,false,false] },
            { id: 'placeholder2', hand: ['?','?','?','?'], revealed: [false,false,false,false] }
        ];
    }
    const meIdx = players.findIndex(p => p.id === window.currentPlayerId);
    const rotated = [];
    if (meIdx === -1) { players.forEach(p => rotated.push(p)); }
    else { for (let i = 0; i < players.length; i++) rotated.push(players[(meIdx + i) % players.length]); }

    const seatNames = seatsForCount(rotated.length);
    for (let i = 0; i < rotated.length; i++) {
        const seat = seatNames[i];
        const container = document.getElementById('player-' + seat);
        const p = rotated[i];
        const seatDiv = document.createElement('div');
        seatDiv.className = 'seat-area';

        const handDiv = document.createElement('div');
        handDiv.style.display = 'flex';
        handDiv.style.gap = '8px';
        const handSize = (p.hand && p.hand.length) ? p.hand.length : 0;
        for (let idx = 0; idx < handSize; idx++) {
            const val = (p.hand && p.hand[idx] != null) ? p.hand[idx] : null;
            const revealed = p.revealed && p.revealed[idx];
            const btn = document.createElement('button');
            btn.className = 'card-btn';
            btn.dataset.idx = idx;
            // show value only if permanently revealed or temporarily exposed by a peek
            let visible = false;
            if (revealed) visible = true;
            if (window._tempReveals && window._tempReveals[p.id] && window._tempReveals[p.id][idx]) visible = true;
            btn.textContent = visible ? (val != null ? String(val) : '_') : '?';
            if (seat === 'bottom') {
                btn.onclick = () => {
                    // pair claim selection mode
                    if (window._pairClaimMode) {
                        window._pairSelected = window._pairSelected || [];
                        const i = window._pairSelected.indexOf(idx);
                        if (i === -1) window._pairSelected.push(idx);
                        else window._pairSelected.splice(i, 1);
                        btn.classList.toggle('selected', i === -1);
                        // update confirm button state
                        if (window._pairConfirmBtn) {
                            window._pairConfirmBtn.disabled = !(window._pairSelected.length >= 2 && window._pairSelected.length <= 4);
                        }
                        return;
                    }
                    if (window._pendingDraw) {
                        sendWSMessage({ type: 'player_action', lobbyId: gameState.lobbyId, actionType: 'resolve_draw', payload: { action: 'swap', cardIndex: idx } });
                        window._pendingDraw = null; return;
                    }
                    if (window._takeDiscardMode) {
                        sendWSMessage({ type: 'player_action', lobbyId: gameState.lobbyId, actionType: 'take_discard', payload: { cardIndex: idx } });
                        window._takeDiscardMode = false; const log = document.getElementById('game-log'); if (log) log.textContent = ''; return;
                    }
                };
            }
            handDiv.appendChild(btn);
        }

        if (seat === 'bottom') {
            const actions = document.createElement('div');
            actions.style.marginTop = '8px';
            actions.style.display = 'flex';
            actions.style.gap = '8px';

            if (p.initialPeeksRemaining && p.initialPeeksRemaining > 0) {
                const peekRow = document.createElement('div');
                peekRow.style.display = 'flex';
                peekRow.style.gap = '6px';
                const maxPeek = Math.max(0, p.hand ? p.hand.length : 4);
                for (let j = 0; j < maxPeek; j++) {
                    const pb = document.createElement('button'); pb.className = 'card-btn'; pb.textContent = 'Peek ' + j;
                    pb.onclick = () => sendWSMessage({ type: 'player_action', lobbyId: gameState.lobbyId, actionType: 'peek_initial', payload: { index: j } });
                    peekRow.appendChild(pb);
                }
                actions.appendChild(peekRow);
            }

            const cabo = document.createElement('button'); cabo.className = 'big-btn'; cabo.textContent = 'Call Cabo';
            cabo.onclick = () => sendWSMessage({ type: 'player_action', lobbyId: gameState.lobbyId, actionType: 'call_cabo' });
            // enable only when it's your turn
            const currentPlayerId = gameState.players && gameState.players[gameState.currentTurn] ? gameState.players[gameState.currentTurn].id : null;
            const isMyTurn = (currentPlayerId === window.currentPlayerId);
            cabo.disabled = !isMyTurn;
            // if there is a pending draw, show pair-claim option
            if (window._pendingDraw) {
                const pairBtn = document.createElement('button'); pairBtn.className = 'card-btn'; pairBtn.textContent = 'Pair Claim';
                pairBtn.onclick = () => {
                    window._pairClaimMode = true;
                    window._pairSelected = [];
                    renderPairUI();
                };
                actions.appendChild(pairBtn);
            }
            actions.appendChild(cabo);

            seatDiv.appendChild(handDiv); seatDiv.appendChild(actions);
        } else {
            seatDiv.appendChild(handDiv);
        }

        if (container) container.appendChild(seatDiv);
    }

    // center controls
    const deckBtn = document.getElementById('deckBtn');
    const pileBtn = document.getElementById('pileBtn');
    // show deck count and top of pile; enable buttons to be clickable for testing
    if (deckBtn) {
        deckBtn.textContent = `Deck (${(gameState.deck||[]).length})`;
        deckBtn.disabled = false;
        deckBtn.onclick = () => sendWSMessage({ type: 'player_action', lobbyId: gameState.lobbyId, actionType: 'draw' });
    }
    if (pileBtn) {
        const top = (gameState.discardPile && gameState.discardPile.length>0) ? gameState.discardPile[gameState.discardPile.length-1] : 'Empty';
        pileBtn.textContent = `Pile (${top})`;
        pileBtn.disabled = false;
        pileBtn.onclick = () => { window._takeDiscardMode = true; const log = document.getElementById('game-log'); if (log) log.textContent = 'Click a card to swap with the top of the pile.'; };
    }

    // pending draw resolution
    const log = document.getElementById('game-log'); if (result && log) log.textContent = result;
    if (window._pendingDraw && log) {
        log.innerHTML = '';
        const panel = document.createElement('div'); panel.style.marginTop = '8px'; panel.innerHTML = `You drew: <b>${window._pendingDraw}</b>`;
        const discardBtn = document.createElement('button'); discardBtn.className = 'card-btn'; discardBtn.textContent = 'Discard';
        discardBtn.onclick = () => { sendWSMessage({ type: 'player_action', lobbyId: gameState.lobbyId, actionType: 'resolve_draw', payload: { action: 'discard' } }); window._pendingDraw = null; log.textContent = ''; showGame(gameState); };
        panel.appendChild(discardBtn); log.appendChild(panel);
    }
}
