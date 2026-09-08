# Chat Window Override

This page is the approved implementation direction for the native Windows chat shell. It overrides generic landing-page recommendations in `MASTER.md`.

## Product thesis

Qwen Local Chat is a quiet local developer tool: the conversation is the product, decoration is a restrained depth cue, and every state must remain readable while the window is resized, scrolled, minimized, restored, or displayed at 150% DPI.

## Native stack

- WinUI 3 / XAML, Windows App SDK, C#.
- Use WinUI controls and `ThemeResource` brushes before authoring a custom control.
- Keep the existing `QwenLocalChat.Core` service, MemOS, model process, settings, input history, and log contracts; only the view layer moves.
- Do not load remote fonts, images, CSS, JavaScript, or network assets.

## Palette and typography

| Token | Value | Use |
|---|---|---|
| `WindowBackgroundBrush` | `#0B0C0F` | Window and quiet space |
| `SurfaceBrush` | `#111318` | Transcript and input surfaces |
| `SurfaceRaisedBrush` | `#171A20` | Focused/hovered controls |
| `BorderBrush` | `#2A2F38` | 1px borders only |
| `TextPrimaryBrush` | `#F4F6F8` | Conversation and headings |
| `TextSecondaryBrush` | `#A6AFBC` | Metadata and helper text |
| `TextMutedBrush` | `#707A88` | Inactive states |
| `AccentBrush` | `#D6DCE5` | Primary action and focus ring |
| `DangerBrush` | `#FF6B6B` | Errors only |

- Use the platform Segoe UI / Segoe Fluent Icons family; no web-font import.
- Heading: 20–22 px, semibold. Body: 15–16 px, regular. Metadata: 12–13 px, semibold with increased tracking.
- Body text must meet 4.5:1 contrast against its immediate surface.

## Window budget and layout

```text
┌──────────────────────────────────────────────────────────────┐
│ title / model status                         memory  log      │
├──────────────────────────────────────────────────────────────┤
│                                                              │
│ fixed decoration layer (not scrollable)                     │
│   ┌──────────────────────────────────────────────────────┐   │
│   │ transcript ListView / ItemsRepeater (scrolls alone)  │   │
│   └──────────────────────────────────────────────────────┘   │
│                                                              │
├──────────────────────────────────────────────────────────────┤
│ message TextBox + send                                      │
│ MemOS toggle     log toggle                 clear / open log │
└──────────────────────────────────────────────────────────────┘
```

- Start around 980 × 720 DIPs; derive the final size from the widest action row and the transcript/input minimums.
- The transcript owns scrolling. Do not wrap `ListView` in another `ScrollViewer`.
- The decoration is one fixed, non-interactive element behind the transcript. It must never be inside an item template or scroll buffer.
- One visual owner presents each pixel. Do not mix a second overlay window, `CreateGraphics`, or stale bitmap snapshots with XAML composition.

## Decoration contract

- Use one static, low-frequency background layer: a soft diagonal graphite light band with sparse small nodes and short traces.
- No concentric radial rings, repeated tiled ornaments, animated scanlines, or per-message decoration.
- Decoration opacity is subordinate to text; it must remain visible in a sampled empty transcript region without crossing glyphs with enough contrast to reduce readability.
- Background is allowed to be absent while the window is initializing, but the first stable frame must show the same fixed layer as subsequent frames.

## Interaction and accessibility

- Every actionable control gets a stable `AutomationProperties.AutomationId` and visible accessible name.
- `Enter` sends, `Shift+Enter` inserts a newline, Up/Down navigate input history when the message box is focused and the caret is at a boundary.
- Keep keyboard focus visible; never remove the system focus cue without replacing it with an equivalent contrast-safe cue.
- Use `InfoBar` for non-blocking model/MemOS status; use `ContentDialog` only for destructive or blocking choices.
- Use subtle 120–180 ms opacity/color transitions only for state feedback; respect reduced-motion settings.

## Acceptance gates

1. Static screenshot: readable text, no clipping, no duplicate decoration, and the fixed background is visible in the same viewport.
2. Temporal scroll: 20 rapid wheel/trackpad changes produce no duplicate, stale, or overlapping text/background frame.
3. Minimize/restore: capture from the first visible frame until stable; no partial controls or background flash is accepted.
4. DPI: 96%, 120%, and 150% preserve the same hierarchy and action-row geometry.
5. UIA: title, transcript, message box, send, both toggles, clear, and open-log controls are discoverable and invokable.

