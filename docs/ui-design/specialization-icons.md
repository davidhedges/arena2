# Form and school icons

The 18 specialization IDs in `server/src/combat_build_v2_catalog.shared.json` each have a 128 × 128 sprite in `Assets/Arena/Resources/UI/AbilityIcons/SPECIALIZATION`. The picker, build overview, editor heading, save summary, and Hub resolve the same sprite by specialization ID.

Arcana reuses the existing `COMBAT_DISCIPLINE_SWITCH/ARCANA.png` artwork. The other 17 images were made with the built-in image generation tool, one image per prompt. Full-square outputs were resized with `sips` without cropping; each installed sprite has a unique asset GUID and sprite ID. Original assets were preserved.

Visual references inspected: `COMBAT_DISCIPLINE_SWITCH/ARCANA.png`, `COMBAT_DISCIPLINE_SWITCH/WAR.png`, and `ABILITY/BLIGHT_TOXIC_WEAPON.png`. The older prototype `school-necromancy.png` was inspected but not reused.

## Generation prompts

Each exact prompt is the shared text below, followed by a newline and `Subject: ` plus the corresponding subject in the table.

Use case: stylized-concept. Asset type: a single square fantasy RPG specialization icon for a polished dark mineral-and-brass game UI. Match a cohesive set of painterly game ability icons: near-black opaque background, one bold central weapon or magical emblem, finely rendered worn steel, bright colored rim light and a few controlled energy wisps. Strong readable silhouette at 48 pixels. Frontal or slight three-quarter view, centered subject occupying 75% of the square, 12% dark breathing room on all sides. High contrast, elegant, dramatic, substantial forms rather than filigree. No lettering, numbers, UI frame, border, watermark, scene, person, or surrounding objects. Full square image.

| Specialization / installed filename | Subject prompt |
| --- | --- |
| `DAGGERS_BLADEDANCER.png` | Duelist: two elegant short silver dueling daggers crossing asymmetrically like a precise parry, violet and cool lavender energy tracing one fluid crescent behind the blades; pointed upward. Communicate agility and perfect timing. |
| `DAGGERS_EXECUTIONER.png` | Heartseeker: one long needle-point dagger piercing a small faceted crimson heart-shaped gemstone, bold single downward thrust, dark violet wisps and a narrow ruby glow. Stylized emblem, no organic heart or gore. Communicate precision and lethal focus. |
| `DAGGERS_SHADOW.png` | Dreadfang: two curved fang-like dark steel daggers arranged as the jaws of a striking serpent, a suggestion of a shadow serpent curling between the blades, purple smoke and small toxic magenta highlights. Communicate stalking and menace. |
| `TWO_HANDED_SWORD_VANGUARD.png` | Vanguard: a broad upright silver greatsword rising through an amber-gold forward sweeping crescent, strong triangular composition, a bright golden point at its tip. Communicate decisive advancing momentum. |
| `TWO_HANDED_SWORD_REAVER.png` | Reaver: one brutal broad dark-steel greatsword with a hooked cleaver-like tip angled from bottom-left to top-right, a red ember glowing inside a fracture along its blade and one blood-red crescent of energy curling around it. Warm copper metal details. Communicate attrition and relentless power; no gore. |
| `TWO_HANDED_SWORD_BERSERKER.png` | Berserker: two heavy steel battle axes crossed in a powerful X, brass edges lit by a controlled fierce amber and red-orange flame bursting between the axe heads, rugged solid shapes and blackened metal. Communicate furious aggression. |
| `SWORD_AND_SHIELD_GUARDIAN.png` | Guardian: a broad upright worn silver kite shield, bold raised protective central ridge and a single small ice-blue gemstone, an icy blue curved ward embracing its outer silhouette, warm aged brass trim. Communicate stalwart protection; no text or heraldic letters. |
| `SWORD_AND_SHIELD_VINDICATOR.png` | Vindicator: a silver one-handed sword crossing diagonally in front of a compact steel heater shield, a strong warm gold flare at the point of crossing, golden energy sweeping outward like a decisive counterattack. Aged brass and ivory highlights. Distinct sword-and-shield silhouette. |
| `SWORD_AND_SHIELD_TEMPLAR.png` | Templar: a compact worn silver shield bearing a raised upright sword crest, two broad stylized metallic wings emerging from its shoulders and a slender pale-gold halo behind it. Warm ivory and quiet gold light, crisp symmetric silhouette. Communicate sacred resolve and protection. The wings are part of the emblem, not a character. |
| `ARCHER_BOW_MARKSMAN.png` | Marksman: a slender recurved bow drawn taut with one bright silver arrow aimed upward, a small subtle green circular aim marker aligned behind the arrowhead. Dark seasoned wood, aged brass fittings, moss-green and pale lime energy. Crisp precise triangular arrowhead, controlled simple silhouette. Communicate accuracy. |
| `ARCHER_BOW_SKIRMISHER.png` | Skirmisher: a compact recurved hunting bow angled diagonally, with a single broad-feathered silver arrow sweeping past it in an S-shaped trail of pale green wind. Olive wood and dark steel, muted emerald and lime rim light. Give the arrow a clearly visible feathered tail; communicate nimble movement and quick shots. |
| `ARCHER_BOW_VOLLEY.png` | Volley: five distinct silver-tipped arrows fanning outward from a single low central point like a crown, each arrowhead broad and readable, weathered brass bindings and dark wood, restrained moss-green wind trails following the fan. Strong five-pronged silhouette. Communicate a barrage of arrows. |
| `BLIGHT.png` | Blight school: a single jagged ice crystal wrapped tightly in a few black thorn vines, sickly green venom glowing inside the blue-white frosted crystal and one small emerald droplet hanging below. Cold pale cyan edges and luminous toxic green core. Strong compact silhouette that communicates frost, decay and debilitating curses. |
| `MORTALITY.png` | Mortality school: a small ancient ivory skull suspended above a dark steel hourglass, pale violet soul-light flowing through the glass like sand, one restrained spectral lavender wisp around the combined emblem. Strong skull and hourglass silhouette. Communicate life, death and stolen vitality; no gore, no full skeleton. |
| `RUIN.png` | Ruin school: a floating angular obsidian orb cracking open around a brilliant orange molten core, several large stone facets separated by radiant fissures, one electric golden-white lightning arc and a compact curl of red-orange flame. Communicate explosive fire and lightning through a single unmistakable shattered-orb silhouette. |
| `DIVINITY.png` | Divinity school: a sculpted radiant golden sunburst emblem with one brilliant ivory central diamond and eight substantial tapered rays, small silver-gold wings cupping its lower half. A quiet warm golden halo, luminous ivory highlights and aged gold metal. Communicate healing, holy light and grace; no shield, sword or religious text. |
| `PRIMAL.png` | Primal school: an ancient faceted stone seed held in a pair of curling oak roots, a single bright emerald leaf unfurling from its top, with one powerful turquoise water-and-wind spiral circling the stone. Weathered gray rock and bronze-brown roots, emerald and teal highlights. Compact elemental nature emblem with a broad readable silhouette; communicate earth, growth, wind and water. |

## Checks

- Every current catalog specialization has a matching sprite; the 18 installed sprites use the same import settings as existing ability icons.
- All icons were inspected at native 128 × 128 and at their 48 × 48 display size.
- Runtime, editor, and test assemblies compile; 54 standalone UI tests pass. The test requiring Unity’s native animation runtime is excluded from that runner.
- No Unity batch mode or browser launch was used for this change. In-game visual verification remains pending.
