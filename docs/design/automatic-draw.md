# Automatic draw design

## Problem

When the local player entered Draw Phase, Duel Links waited for a draw input that the mod did not expose. The game eventually advanced after about 21 seconds. Opponent turns did not wait for local input.

## Runtime behavior

`AutomaticDrawController` permits one automatic attempt during each continuous local Draw Phase. It resets after the duel leaves that input state. `Space` provides an explicit retry while Draw Phase is still waiting.

The adapter sends the normal `Engine.CommandType.Draw` command. It then performs the same `DrawOperationMultiDraw` pointer gesture used by the game's visible draw prompt and advances a lingering `WaitDetail` prompt when needed. These steps address separate layers of the game's duel presentation; none moves a card directly or forces Main Phase.

The duel engine still owns card movement, draw-triggered effects, activation prompts, chains, and phase changes. Runtime logs showed the path reach Main Phase in about 2.25 seconds instead of the previous 21.3 seconds.

## Retry and failure rules

- Only the local player's active Draw Phase is eligible.
- Automatic dispatch runs once per continuous eligible phase.
- A thrown dispatch clears the attempt guard so `Space` can retry.
- Pointer lookup failure does not repeatedly click every frame.
- Leaving Draw Phase clears controller state.

## Tests

Unit tests cover controller eligibility, one-shot behavior, reset, retries, thrown dispatch, pointer timing, and delayed detail advancement. Runtime verification remains necessary because Unity objects and duel presentation timing are not available to the portable test project.
