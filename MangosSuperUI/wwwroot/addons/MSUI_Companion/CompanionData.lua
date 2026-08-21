-- MSUI_Companion ability catalog
-- PLACEHOLDER. This file is regenerated from the character database every time the
-- Downloads page is opened (DownloadsController.RefreshCompanionData), so the copy
-- inside a downloaded ZIP always reflects what each character knew at download time.
--
-- Shape:
--   MSUI_COMPANION_SPELLS["Charactername"] = {
--       class = 1, level = 34,
--       spells = { { id = 133, name = "Fireball", rank = "Rank 6", icon = "spell_fire_flamebolt", level = 34 }, ... }
--   }
--
-- Re-download after levelling to pick up new ranks. Until then the bar shows the
-- older set; the server independently refuses any spell the character does not
-- actually know, so a stale catalog can never invent an ability.

MSUI_COMPANION_SPELLS = {};
