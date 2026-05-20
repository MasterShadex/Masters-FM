# v14 Design Tokens (extracted from WPF Theme XAML)

**Source files (authoritative):**
- `src/tray_csharp/Theme/Colors.xaml`
- `src/tray_csharp/Theme/Typography.xaml`
- `src/tray_csharp/Theme/AppDialogStyle.xaml`
- `src/tray_csharp/Theme/Animations.xaml`
- `src/tray_csharp/Theme/Buttons.xaml` (radius + padding for buttons)
- `src/tray_csharp/Theme/Cards.xaml` (radius + padding for cards)
- `src/tray_csharp/Theme/Inputs.xaml` (radius + padding for inputs)

Stage 7.13 STEP 0 inventory. These tokens drive STEPs 2-8.

---

## Colors

### Brand purple (primary brand color)
| Token | Hex | Usage |
|---|---|---|
| `--brand-base` (BrandPurpleBase) | `#8B5CF6` | Primary button bg, sidebar active border, accent bar mid-stops, input focus border |
| `--brand-deep` (BrandPurpleDeep) | `#7C3AED` | Hover state for primary buttons |
| `--brand-glow` (BrandPurpleGlow) | `#A78BFA` | Accent bar middle gradient stop, hover shadow color |

### Surfaces (background layers, darkest → lightest)
| Token | Hex | Usage |
|---|---|---|
| `--surface-0` (Surface0) | `#0A0A0F` | App background, main content area |
| `--surface-1` (Surface1) | `#14141B` | Cards, dialog body, sidebar |
| `--surface-2` (Surface2) | `#1F1F2A` | Inputs, nested elements, hover background |
| `--surface-3` (Surface3) | `#2A2A38` | (rarely used; deeper nesting) |

### Text
| Token | Hex | Usage |
|---|---|---|
| `--text-primary` (TextPrimary) | `#F5F5F7` | Headings, primary copy |
| `--text-secondary` (TextSecondary) | `#9999A1` | Subtitles, secondary copy |
| `--text-tertiary` (TextTertiary) | `#5C5C66` | Captions, helper text, placeholders |

### Borders
| Token | Hex | Usage |
|---|---|---|
| `--border-subtle` (BorderSubtle) | `#1A1A24` | Default card/input borders (near-invisible) |
| `--border-focus` (BorderFocus) | `#8B5CF6` | Input focus border (= brand-base) |

### Accent bar (signature top-of-dialog 3px gradient bar)
3-stop horizontal gradient:
- 0%: `#8B5CF6` (BrandPurpleBase)
- 50%: `#A78BFA` (BrandPurpleGlow)
- 100%: `#8B5CF6` (BrandPurpleBase)

CSS:
```css
--accent-bar: linear-gradient(90deg, #8B5CF6 0%, #A78BFA 50%, #8B5CF6 100%);
```

### Semantic tag colors (for badge-like elements; optional in customize.html)
| Token | Hex | Usage |
|---|---|---|
| TagNew | `#22C55E` | green |
| TagImproved | `#38BDF8` | blue |
| TagFixed | `#FB923C` | orange |
| TagRemoved | `#F87171` | red |
| TagNote | `#5C5C66` | neutral grey (= text-tertiary) |

---

## Typography

**Font family:**
- UI: `'Segoe UI', system-ui, -apple-system, sans-serif`
- Mono: `'Cascadia Mono', Consolas, 'Courier New', monospace`

**Scale** (px / weight / line-height):

| Token | Size | Weight | Line-height | Usage |
|---|---:|---|---:|---|
| `--fs-display` | 28 | SemiBold (600) | (default) | Welcome hero, dialog title |
| `--fs-heading` | 18 | SemiBold (600) | (default) | Section headers |
| `--fs-subheading` | 15 | SemiBold (600) | (default) | Tab labels, list group titles |
| `--fs-body-strong` | 14 | SemiBold (600) | (default) | Button labels, important inline |
| `--fs-body` | 14 | Normal (400) | 21 px (1.5x) | Body copy |
| `--fs-caption` | 12 | Normal (400) | 17 px (1.42x) | Helper text, labels |
| `--fs-tiny` | 11 | Medium (500) | (default) | Metadata, badges; UPPERCASE at usage site |
| `--fs-mono` | 13 | Normal (400) | (default) | Device IDs, paths, code |

---

## Spacing (4px rhythm)

Standard increments: `4, 8, 12, 16, 24, 32`.

Observed component-level spacing (from WPF theme):
- Button padding: `10 vertical / 24 horizontal` (= 10px top/bottom, 24px left/right)
- Card padding: `16` (all sides)
- Input padding: `10 vertical / 14 horizontal`
- Dialog title bar height: `44 px`
- Accent bar height: `3 px`

---

## Border radius

| Component | Radius |
|---|---|
| Dialog body (outer) | `12 px` |
| Cards / panels | `12 px` |
| Buttons (primary + secondary) | `8 px` |
| Inputs / selects / sliders track | `6 px` |
| Dropdown popup container | `8 px` |
| Dropdown item rows | `4 px` |
| Pills / tags / chips | `999 px` (full pill) OR `4 px` |

---

## Animation

### Durations
| Token | ms | Usage |
|---|---:|---|
| `--dur-press` | 80 | Button press depress |
| `--dur-fast` | 150 | Hover transitions |
| `--dur-standard` | 220 | Default animation (state changes) |
| `--dur-slow` | 280 | Dialog open / larger moves |
| `--dur-accent-bar` | 8000 (8s) | Accent bar shimmer loop |

### Easings (WPF → CSS cubic-bezier approximations)
| Name | WPF | CSS |
|---|---|---|
| StandardEasing | QuadraticEase EaseInOut | `cubic-bezier(0.4, 0, 0.2, 1)` |
| EaseOutEasing | QuadraticEase EaseOut | `cubic-bezier(0.0, 0, 0.2, 1)` |
| SpringEasing | ElasticEase EaseOut, Spring=3 | `cubic-bezier(0.16, 1, 0.3, 1)` |
| EaseInEasing | QuadraticEase EaseIn | `cubic-bezier(0.4, 0, 1, 1)` |

### Reduced motion
In WPF: App.xaml.cs overrides all Duration resources to `0:0:0` (instant) when `App.IsReducedMotion == true`.

For customize.html, the CSS equivalent:
```css
@media (prefers-reduced-motion: reduce) {
    *, *::before, *::after {
        animation-duration: 0.01ms !important;
        animation-iteration-count: 1 !important;
        transition-duration: 0.01ms !important;
    }
}
```

---

## Shadows (drop shadow effects)

CSS box-shadow approximations of the WPF DropShadowEffects:

| Token | WPF (Blur / Depth / Opacity) | CSS |
|---|---|---|
| Dialog | 40 / 20 / 0.55 | `0 20px 40px rgba(0,0,0,0.55)` |
| Card resting | 4 / 1 / 0.4 | `0 1px 4px rgba(0,0,0,0.4)` |
| Card hover | 18 / 4 / 0.5 | `0 4px 18px rgba(0,0,0,0.5)` |
| Button hover | 18 / 4 / 0.35 (brand color) | `0 4px 18px rgba(139, 92, 246, 0.35)` |
| Input focus | 8 / 0 / 0.45 (brand color) | `0 0 8px rgba(139, 92, 246, 0.45)` |

---

## CSS variable mapping for customize.html

The canonical token set that STEP 2 will inject into `customize.html`'s `:root` block:

```css
:root {
    /* Surfaces */
    --bg:           #0A0A0F;    /* Surface0 */
    --surface-1:    #14141B;    /* Surface1 (cards, sidebar, dialog body) */
    --surface-2:    #1F1F2A;    /* Surface2 (inputs, hover bg) */
    --surface-3:    #2A2A38;    /* Surface3 (deeper nesting) */

    /* Text */
    --text-primary:    #F5F5F7;
    --text-secondary:  #9999A1;
    --text-tertiary:   #5C5C66;

    /* Brand */
    --brand-base:   #8B5CF6;
    --brand-deep:   #7C3AED;
    --brand-glow:   #A78BFA;

    /* Borders */
    --border-subtle:  #1A1A24;
    --border-focus:   #8B5CF6;

    /* Accent bar gradient */
    --accent-bar: linear-gradient(90deg, #8B5CF6 0%, #A78BFA 50%, #8B5CF6 100%);

    /* Legacy aliases (preserve existing class consumers without mass rename) */
    --accent:        var(--brand-base);
    --accent-hover:  var(--brand-deep);
    --sidebar-bg:    var(--surface-1);
    --text-muted:    var(--text-tertiary);
    --border:        var(--border-subtle);
    --hover:         var(--surface-2);

    /* Typography */
    --font-ui:    'Segoe UI', system-ui, -apple-system, sans-serif;
    --font-mono:  'Cascadia Mono', Consolas, 'Courier New', monospace;
    --fs-display:       28px;
    --fs-heading:       18px;
    --fs-subheading:    15px;
    --fs-body-strong:   14px;
    --fs-body:          14px;
    --fs-caption:       12px;
    --fs-tiny:          11px;
    --fs-mono:          13px;
    --fw-regular:  400;
    --fw-medium:   500;
    --fw-semibold: 600;
    --lh-body:     1.5;
    --lh-caption:  1.42;

    /* Radius */
    --r-card:    12px;
    --r-button:  8px;
    --r-input:   6px;
    --r-pill:    999px;
    --r-tiny:    4px;

    /* Spacing rhythm */
    --sp-1:  4px;
    --sp-2:  8px;
    --sp-3:  12px;
    --sp-4:  16px;
    --sp-5:  24px;
    --sp-6:  32px;

    /* Animation */
    --dur-press:     80ms;
    --dur-fast:      150ms;
    --dur-standard:  220ms;
    --dur-slow:      280ms;
    --ease-standard: cubic-bezier(0.4, 0, 0.2, 1);
    --ease-out:      cubic-bezier(0.0, 0, 0.2, 1);
    --ease-spring:   cubic-bezier(0.16, 1, 0.3, 1);

    /* Shadows */
    --shadow-dialog:       0 20px 40px rgba(0, 0, 0, 0.55);
    --shadow-card-rest:    0 1px 4px rgba(0, 0, 0, 0.40);
    --shadow-card-hover:   0 4px 18px rgba(0, 0, 0, 0.50);
    --shadow-button-hover: 0 4px 18px rgba(139, 92, 246, 0.35);
    --shadow-input-focus:  0 0 8px rgba(139, 92, 246, 0.45);
}
```

---

## Accent bar (signature visual)

```html
<div class="accent-bar"></div>
```

```css
.accent-bar {
    position: absolute;
    top: 0;
    left: 0;
    right: 0;
    height: 3px;
    background: var(--accent-bar);
    background-size: 200% 100%;
    animation: accent-drift 8s linear infinite;
    z-index: 1000;
}
@keyframes accent-drift {
    0%   { background-position:   0% 50%; }
    100% { background-position: 200% 50%; }
}
@media (prefers-reduced-motion: reduce) {
    .accent-bar { animation: none; }
}
```

Note: WPF version uses an opacity-pulse shimmer (white highlight sliding across) instead of background-position drift. For CSS, the gradient-drift approach is simpler and visually equivalent at glance.

---

*Extracted 2026-05-20 by Stage 7.13 STEP 0. Authoritative for the rebuild.*
