"""Writes the character catalogs: appearance.json, clothing.json and animations.json.

Everything a person can look like, wear and do, as data. Edit the tables here and run:

    python3 Tools/Characters/generate_catalogs.py          # write the JSON
    python3 Tools/Characters/generate_catalogs.py --check  # fail if the committed JSON is out of date

All names and designs are original (no real brands). Prices are US dollars before the city's price level.
"""
import json
import os
import sys

DATA = os.path.join(os.path.dirname(__file__), "..", "..", "Game", "Assets", "StreamingAssets", "Data")

# ----------------------------------------------------------------------------------------------- appearance

FACE_MORPHS = [
    # region, id, label, natural spread
    ("Head", "head_size", "Head size", 0.10), ("Head", "head_width", "Head width", 0.14),
    ("Head", "face_length", "Face length", 0.16), ("Head", "temple_width", "Temple width", 0.12),
    ("Forehead", "forehead_height", "Forehead height", 0.18), ("Forehead", "forehead_slope", "Forehead slope", 0.16),
    ("Brow", "brow_height", "Brow height", 0.16), ("Brow", "brow_ridge", "Brow ridge", 0.2),
    ("Brow", "brow_angle", "Brow angle", 0.16),
    ("Eyes", "eye_size", "Eye size", 0.16), ("Eyes", "eye_spacing", "Eye spacing", 0.16),
    ("Eyes", "eye_height", "Eye height", 0.12), ("Eyes", "eye_tilt", "Eye tilt", 0.2),
    ("Eyes", "eye_depth", "Eye depth", 0.16), ("Eyes", "eyelid_hood", "Hooded lids", 0.22),
    ("Eyes", "epicanthic_fold", "Inner eyelid fold", 0.25), ("Eyes", "eye_bags", "Under-eye bags", 0.2),
    ("Nose", "nose_width", "Nose width", 0.2), ("Nose", "nose_length", "Nose length", 0.18),
    ("Nose", "nose_bridge_height", "Bridge height", 0.2), ("Nose", "nose_bridge_curve", "Bridge curve", 0.2),
    ("Nose", "nose_tip_height", "Tip height", 0.16), ("Nose", "nose_tip_size", "Tip size", 0.18),
    ("Nose", "nostril_flare", "Nostril flare", 0.2), ("Nose", "nose_crookedness", "Crooked nose", 0.06),
    ("Cheeks", "cheekbone_height", "Cheekbone height", 0.16), ("Cheeks", "cheekbone_width", "Cheekbone width", 0.16),
    ("Cheeks", "cheek_fullness", "Cheek fullness", 0.2), ("Cheeks", "cheek_hollow", "Hollow cheeks", 0.14),
    ("Cheeks", "dimples", "Dimples", 0.15),
    ("Mouth", "mouth_width", "Mouth width", 0.16), ("Mouth", "mouth_height", "Mouth height", 0.1),
    ("Mouth", "lip_upper_fullness", "Upper lip", 0.2), ("Mouth", "lip_lower_fullness", "Lower lip", 0.2),
    ("Mouth", "lip_corner_tilt", "Lip corners", 0.14), ("Mouth", "philtrum_depth", "Philtrum", 0.14),
    ("Mouth", "overbite", "Overbite", 0.1),
    ("Jaw", "jaw_width", "Jaw width", 0.18), ("Jaw", "jaw_angle", "Jaw angle", 0.16),
    ("Jaw", "jaw_definition", "Jawline definition", 0.18), ("Jaw", "double_chin", "Double chin", 0.12),
    ("Chin", "chin_length", "Chin length", 0.16), ("Chin", "chin_width", "Chin width", 0.16),
    ("Chin", "chin_projection", "Chin projection", 0.16), ("Chin", "chin_cleft", "Chin cleft", 0.12),
    ("Ears", "ear_size", "Ear size", 0.16), ("Ears", "ear_angle", "Ears stick out", 0.2),
    ("Ears", "ear_lobe", "Lobe size", 0.18), ("Ears", "ear_point", "Ear tip shape", 0.1),
    ("Neck", "neck_thickness", "Neck thickness", 0.16), ("Neck", "adams_apple", "Adam's apple", 0.2),
]

BODY_MORPHS = [
    ("Build", "body_fat", "Body fat", 0.2), ("Build", "muscle", "Muscle", 0.2),
    ("Build", "posture", "Posture (upright ↔ slouched)", 0.14),
    ("Torso", "shoulder_width", "Shoulder width", 0.16), ("Torso", "chest_size", "Chest", 0.18),
    ("Torso", "bust_size", "Bust", 0.22), ("Torso", "waist_width", "Waist", 0.18),
    ("Torso", "stomach", "Stomach", 0.2), ("Torso", "torso_length", "Torso length", 0.12),
    ("Torso", "back_width", "Back width", 0.14),
    ("Hips", "hip_width", "Hips", 0.18), ("Hips", "glutes", "Glutes", 0.18),
    ("Arms", "arm_length", "Arm length", 0.08), ("Arms", "upper_arm_size", "Upper arms", 0.18),
    ("Arms", "forearm_size", "Forearms", 0.16), ("Arms", "hand_size", "Hands", 0.12),
    ("Legs", "leg_length", "Leg length", 0.1), ("Legs", "thigh_size", "Thighs", 0.18),
    ("Legs", "calf_size", "Calves", 0.16), ("Legs", "foot_size", "Feet", 0.12),
    ("Neck", "neck_length", "Neck length", 0.12),
]


def style(sid, label, tags, commonness=1.0):
    return {"Id": sid, "Label": label, "Tags": tags, "Commonness": commonness}


HAIR = [
    style("bald", "Bald", ["none", "bald", "masculine"], 0.5),
    style("shaved", "Shaved (clippers)", ["short", "masculine"], 0.7),
    style("buzz", "Buzz cut", ["short"], 1.2),
    style("crew", "Crew cut", ["short", "masculine"], 1.4),
    style("fade_low", "Low fade", ["short", "masculine"], 1.3),
    style("fade_high", "High fade", ["short", "masculine"], 1.1),
    style("taper_curls", "Taper with curls on top", ["short", "curly", "masculine"], 0.9),
    style("caesar", "Caesar", ["short", "masculine"], 0.6),
    style("crop_textured", "Textured crop", ["short", "masculine"], 1.0),
    style("side_part", "Side part", ["short", "masculine"], 1.0),
    style("slick_back", "Slicked back", ["medium", "masculine"], 0.5),
    style("pompadour", "Pompadour", ["medium", "masculine"], 0.3),
    style("quiff", "Quiff", ["medium"], 0.5),
    style("curtains", "Curtains", ["medium"], 0.5),
    style("messy_medium", "Messy medium", ["medium", "wavy"], 0.8),
    style("shag", "Shag", ["medium", "wavy"], 0.6),
    style("mullet_modern", "Modern mullet", ["medium"], 0.25),
    style("man_bun", "Bun (tied back)", ["long", "masculine"], 0.4),
    style("waves_360", "360 waves", ["short", "coily", "masculine"], 0.8),
    style("cornrows", "Cornrows", ["braided", "coily"], 0.7),
    style("twists_short", "Short twists", ["short", "coily"], 0.7),
    style("locs_short", "Short locs", ["medium", "coily"], 0.5),
    style("locs_long", "Long locs", ["long", "coily"], 0.5),
    style("afro_small", "Small afro", ["short", "coily"], 0.6),
    style("afro_large", "Full afro", ["medium", "coily"], 0.4),
    style("twist_out", "Twist-out", ["medium", "coily", "feminine"], 0.5),
    style("box_braids", "Box braids", ["long", "braided", "feminine"], 0.8),
    style("knotless_braids", "Knotless braids", ["long", "braided", "feminine"], 0.6),
    style("bantu_knots", "Bantu knots", ["short", "coily", "feminine"], 0.2),
    style("silk_press", "Silk press", ["long", "straight", "feminine"], 0.5),
    style("pixie", "Pixie cut", ["short", "feminine"], 0.6),
    style("bob", "Bob", ["medium", "straight", "feminine"], 1.0),
    style("lob_waves", "Long bob, waves", ["medium", "wavy", "feminine"], 0.9),
    style("long_straight", "Long, straight", ["long", "straight", "feminine"], 1.2),
    style("long_waves", "Long waves", ["long", "wavy", "feminine"], 1.2),
    style("long_curls", "Long curls", ["long", "curly", "feminine"], 0.9),
    style("ponytail_high", "High ponytail", ["long", "feminine"], 0.9),
    style("ponytail_low", "Low ponytail", ["long"], 0.8),
    style("bun_messy", "Messy bun", ["long", "feminine"], 1.0),
    style("bun_sleek", "Sleek bun", ["long", "feminine"], 0.6),
    style("half_up", "Half up", ["long", "feminine"], 0.6),
    style("single_braid", "Single braid", ["long", "braided", "feminine"], 0.4),
    style("space_buns", "Space buns", ["long", "feminine"], 0.15),
    style("receding_short", "Receding, short", ["short", "masculine", "older"], 0.8),
    style("thinning_combover", "Thinning, combed over", ["short", "masculine", "older"], 0.4),
    style("silver_bob", "Short set curls", ["short", "curly", "feminine", "older"], 0.6),
]

FACIAL_HAIR = [
    style("none", "Clean shaven", [], 3.0),
    style("stubble_light", "Light stubble", [], 1.4),
    style("stubble_heavy", "Heavy stubble", [], 1.0),
    style("mustache_chevron", "Chevron mustache", [], 0.4),
    style("mustache_pencil", "Pencil mustache", [], 0.15),
    style("mustache_handlebar", "Handlebar mustache", [], 0.08),
    style("goatee", "Goatee", [], 0.6),
    style("circle_beard", "Circle beard", [], 0.6),
    style("van_dyke", "Van Dyke", [], 0.3),
    style("anchor", "Anchor beard", [], 0.2),
    style("chin_strap", "Chin strap", [], 0.3),
    style("soul_patch", "Soul patch", [], 0.1),
    style("short_boxed", "Short boxed beard", [], 0.8),
    style("full_beard", "Full beard", [], 0.7),
    style("long_beard", "Long beard", [], 0.2),
    style("mutton_chops", "Mutton chops", [], 0.05),
]

EYEBROWS = [
    style("natural", "Natural", [], 3.0), style("thin", "Thin", [], 0.6), style("thick", "Thick", [], 1.0),
    style("straight", "Straight", [], 1.0), style("arched", "Arched", [], 1.0), style("soft_angle", "Soft angle", [], 1.0),
    style("bushy", "Bushy", [], 0.5), style("groomed", "Groomed", [], 0.8), style("slit_one", "One slit", [], 0.08),
    style("slit_two", "Two slits", [], 0.05), style("feathered", "Brushed up", [], 0.4),
]

SKIN_DETAILS = [
    style("freckles", "Freckles", [], 0.4), style("moles", "Moles and beauty marks", [], 0.6),
    style("age_lines", "Age lines", [], 1.0), style("blemishes", "Blemishes", [], 0.4),
    style("acne_scars", "Acne scars", [], 0.2), style("cheek_redness", "Flushed cheeks", [], 0.3),
    style("dark_circles", "Dark circles", [], 0.5), style("sun_damage", "Sun spots", [], 0.3),
    style("stretch_marks", "Stretch marks", [], 0.3), style("scar_face", "Facial scar", [], 0.05),
    style("scar_body", "Body scars", [], 0.08), style("vitiligo", "Vitiligo", [], 0.02),
    style("birthmark", "Birthmark", [], 0.06),
]

MAKEUP = [
    style("none", "None", [], 3.0), style("natural", "Natural", [], 1.5), style("soft_glam", "Soft glam", [], 0.7),
    style("smoky", "Smoky eye", [], 0.4), style("winged", "Winged liner", [], 0.5), style("bold_lip", "Bold lip", [], 0.5),
    style("glitter", "Glitter", [], 0.08), style("goth", "Dark", [], 0.08),
]


def color(cid, label, hexv, natural=True, commonness=1.0):
    return {"Id": cid, "Label": label, "Hex": hexv, "Natural": natural, "Commonness": commonness}


HAIR_COLORS = [
    color("black", "Black", "#0F0C0B", True, 3.0), color("off_black", "Off black", "#1C1512", True, 2.5),
    color("dark_brown", "Dark brown", "#2E1D14", True, 2.5), color("brown", "Brown", "#4A2F1E", True, 2.0),
    color("light_brown", "Light brown", "#6B4A2F", True, 1.2), color("dark_blonde", "Dark blonde", "#8A6A43", True, 0.9),
    color("blonde", "Blonde", "#B8955E", True, 0.7), color("platinum", "Platinum", "#DCCFB4", True, 0.2),
    color("auburn", "Auburn", "#6E2F1B", True, 0.4), color("ginger", "Ginger", "#A0461F", True, 0.3),
    color("salt_pepper", "Salt and pepper", "#6F6B67", True, 0.7), color("grey", "Grey", "#9D9A96", True, 0.7),
    color("white", "White", "#DEDCD8", True, 0.4),
    color("burgundy", "Burgundy (dyed)", "#5C1523", False, 0.15), color("honey", "Honey (dyed)", "#B07A3A", False, 0.2),
    color("jet_blue", "Blue-black (dyed)", "#101826", False, 0.1), color("pastel_pink", "Pastel pink (dyed)", "#E4A6B8", False, 0.04),
    color("electric_blue", "Electric blue (dyed)", "#1F57C8", False, 0.03), color("mint", "Mint (dyed)", "#9ED9C0", False, 0.02),
    color("violet", "Violet (dyed)", "#5B3A8C", False, 0.03), color("copper_red", "Copper red (dyed)", "#B3401D", False, 0.06),
]

EYE_COLORS = [
    color("dark_brown", "Dark brown", "#2B1A10", True, 4.0), color("brown", "Brown", "#4B2E1A", True, 3.0),
    color("hazel", "Hazel", "#7A5A2E", True, 1.2), color("amber", "Amber", "#9B6A1F", True, 0.3),
    color("green", "Green", "#4E6B3A", True, 0.5), color("grey", "Grey", "#7C858C", True, 0.3),
    color("blue", "Blue", "#3E6A92", True, 1.0), color("light_blue", "Light blue", "#7EA6C8", True, 0.4),
]

SKIN_TONES = [
    color("porcelain", "Porcelain", "#F3D9C8"), color("fair", "Fair", "#E9C5AB"), color("light", "Light", "#DDB091"),
    color("light_medium", "Light medium", "#C99772"), color("medium", "Medium", "#B07C57"),
    color("tan", "Tan", "#9A6A45"), color("medium_deep", "Medium deep", "#7F5436"), color("deep", "Deep", "#61402A"),
    color("rich", "Rich", "#4A3021"), color("ebony", "Ebony", "#34221A"),
]

TATTOO_ZONES = ["face", "neck_front", "neck_side", "chest", "stomach", "upper_back", "lower_back",
                "left_shoulder", "left_upper_arm", "left_forearm", "left_hand", "left_fingers",
                "right_shoulder", "right_upper_arm", "right_forearm", "right_hand", "right_fingers",
                "left_thigh", "left_calf", "right_thigh", "right_calf", "ankle"]
ARMS = ["left_upper_arm", "left_forearm", "right_upper_arm", "right_forearm", "left_shoulder", "right_shoulder"]
LEGS = ["left_thigh", "left_calf", "right_thigh", "right_calf"]
TORSO = ["chest", "stomach", "upper_back", "lower_back"]
SMALL = ["neck_side", "left_hand", "right_hand", "ankle", "left_forearm", "right_forearm"]


def tattoo(tid, label, style_name, zones):
    return {"Id": tid, "Label": label, "Style": style_name, "Zones": zones}


TATTOOS = [
    tattoo("rose", "Rose", "Traditional", ARMS + LEGS + SMALL + ["chest"]),
    tattoo("anchor", "Anchor", "Traditional", ARMS + LEGS + ["chest"]),
    tattoo("swallows", "Swallows", "Traditional", ["chest", "neck_side", "left_hand", "right_hand"]),
    tattoo("compass", "Compass", "Fine line", ARMS + LEGS + ["chest", "upper_back"]),
    tattoo("dagger_heart", "Dagger through a heart", "Traditional", ARMS + LEGS),
    tattoo("pelican", "Pelican", "Illustrative", ARMS + LEGS + ["upper_back"]),
    tattoo("magnolia", "Magnolia", "Fine line", ARMS + LEGS + TORSO + ["neck_side"]),
    tattoo("koi", "Koi", "Japanese-inspired", ARMS + LEGS + ["upper_back"]),
    tattoo("snake", "Snake", "Blackwork", ARMS + LEGS + ["neck_side"]),
    tattoo("wolf", "Wolf head", "Realism", ARMS + ["chest", "upper_back"]),
    tattoo("lion", "Lion", "Realism", ARMS + ["chest", "upper_back"]),
    tattoo("clock_roses", "Clock and roses", "Black and grey", ARMS + ["chest"]),
    tattoo("praying_hands", "Praying hands", "Black and grey", ARMS + ["chest", "upper_back"]),
    tattoo("skull", "Skull", "Black and grey", ARMS + LEGS + ["left_hand", "right_hand"]),
    tattoo("script_name", "Script name", "Lettering", ARMS + TORSO + ["neck_front", "neck_side"]),
    tattoo("date_numerals", "Date in numerals", "Lettering", ARMS + ["chest", "left_hand", "right_hand"]),
    tattoo("tribal_band", "Tribal band", "Blackwork", ["left_upper_arm", "right_upper_arm", "left_calf", "right_calf"]),
    tattoo("geometric_sleeve", "Geometric sleeve", "Dotwork", ARMS),
    tattoo("mandala", "Mandala", "Dotwork", ARMS + TORSO + LEGS),
    tattoo("butterfly", "Butterfly", "Fine line", SMALL + ARMS + ["lower_back", "chest"]),
    tattoo("crescent_moon", "Crescent moon", "Fine line", SMALL + ARMS),
    tattoo("stars", "Stars", "Fine line", SMALL + ["face", "neck_side"]),
    tattoo("feather", "Feather", "Fine line", ARMS + SMALL + ["lower_back"]),
    tattoo("barbed_band", "Barbed wire band", "Blackwork", ["left_upper_arm", "right_upper_arm"]),
    tattoo("skyline", "Port Arden skyline", "Black and grey", ARMS + ["chest", "upper_back"]),
    tattoo("portrait", "Portrait", "Realism", ARMS + ["chest", "upper_back", "left_thigh", "right_thigh"]),
    tattoo("teardrop", "Teardrop", "Traditional", ["face"]),
    tattoo("cross", "Cross", "Black and grey", ARMS + SMALL + ["chest", "neck_side"]),
    tattoo("heartbeat", "Heartbeat line", "Fine line", SMALL + ARMS + ["chest"]),
    tattoo("knuckle_letters", "Knuckle lettering", "Lettering", ["left_fingers", "right_fingers"]),
    tattoo("finger_rings", "Finger bands", "Fine line", ["left_fingers", "right_fingers"]),
    tattoo("back_piece", "Full back piece", "Japanese-inspired", ["upper_back", "lower_back"]),
    tattoo("chest_eagle", "Eagle across the chest", "Traditional", ["chest", "upper_back"]),
    tattoo("hand_rose", "Rose on the hand", "Black and grey", ["left_hand", "right_hand"]),
    tattoo("neck_script", "Throat lettering", "Lettering", ["neck_front"]),
    tattoo("wave", "Wave", "Japanese-inspired", ARMS + LEGS + ["ankle"]),
]


def appearance():
    morphs = [{"Id": m[1], "Label": m[2], "Group": "Face", "Region": m[0], "NaturalSpread": m[3]} for m in FACE_MORPHS]
    morphs += [{"Id": m[1], "Label": m[2], "Group": "Body", "Region": m[0], "NaturalSpread": m[3]} for m in BODY_MORPHS]
    return {"Morphs": morphs, "HairStyles": HAIR, "FacialHair": FACIAL_HAIR, "Eyebrows": EYEBROWS,
            "HairColors": HAIR_COLORS, "EyeColors": EYE_COLORS, "SkinTones": SKIN_TONES, "SkinDetails": SKIN_DETAILS,
            "Makeup": MAKEUP, "TattooZones": TATTOO_ZONES, "TattooDesigns": TATTOOS}


# ----------------------------------------------------------------------------------------------- clothing

PALETTES = {
    "basics": [("white", "White", "#EDEDEA"), ("black", "Black", "#1B1B1C"), ("heather", "Heather grey", "#8E8F92"),
               ("navy", "Navy", "#1F2A44"), ("olive", "Olive", "#4F5433"), ("burgundy", "Burgundy", "#5B1E28"),
               ("sand", "Sand", "#C9B79A"), ("sky", "Sky blue", "#8FB3D6")],
    "brights": [("red", "Red", "#B3261E"), ("mustard", "Mustard", "#C99A2E"), ("teal", "Teal", "#1F6E6E"),
                ("coral", "Coral", "#E0735C"), ("lilac", "Lilac", "#A897C9"), ("forest", "Forest", "#27452F")],
    "denim": [("light_wash", "Light wash", "#8FA6BF"), ("mid_wash", "Mid wash", "#4C6488"), ("dark_wash", "Dark wash", "#26324A"),
              ("black_denim", "Black", "#232326"), ("white_denim", "White", "#E7E5DF")],
    "chino": [("khaki", "Khaki", "#B59F77"), ("stone", "Stone", "#CFC6B3"), ("olive", "Olive", "#595C3A"),
              ("navy", "Navy", "#232E47"), ("charcoal", "Charcoal", "#3A3B3E")],
    "leather": [("black", "Black leather", "#161413"), ("brown", "Brown leather", "#4A2E1C"), ("tan", "Tan leather", "#9A6B42")],
    "suit": [("charcoal", "Charcoal", "#35373B"), ("navy", "Navy", "#1D2740"), ("black", "Black", "#141416"),
             ("light_grey", "Light grey", "#8C8E91"), ("tan", "Tan", "#A68B66")],
    "metal": [("silver", "Silver", "#C9CCCF", 1.0), ("gold", "Gold", "#D1A94A", 1.6), ("rose_gold", "Rose gold", "#C98E78", 1.4),
              ("gunmetal", "Gunmetal", "#4B4E52", 0.9)],
    "athletic": [("black", "Black", "#18181A"), ("white", "White", "#F0F0EE"), ("red", "Red", "#C0262D"),
                 ("royal", "Royal blue", "#244BA6"), ("volt", "Volt green", "#B8E03A"), ("grey", "Grey", "#8A8C90")],
    "sneaker": [("white", "White", "#F2F2F0"), ("black", "Black", "#1A1A1B"), ("white_red", "White / red", "#F2F2F0", 1.0, "#B3261E"),
                ("grey_blue", "Grey / blue", "#9A9DA2", 1.0, "#2D4F8C"), ("cream_gum", "Cream / gum", "#E8DFCB", 1.0, "#9B6A3C")],
    "earth": [("brown", "Brown", "#5A3E2B"), ("tan", "Tan", "#A07A52"), ("wheat", "Wheat", "#C8A46A"), ("black", "Black", "#1B1918")],
    "hivis": [("yellow", "Hi-vis yellow", "#D7E62B"), ("orange", "Hi-vis orange", "#F07A1A")],
    "scrubs": [("ceil", "Ceil blue", "#7FA5C9"), ("navy", "Navy", "#23305A"), ("wine", "Wine", "#6A2335"), ("teal", "Teal", "#25777A")],
    "sunglass": [("black", "Black / smoke", "#141414"), ("tortoise", "Tortoise / brown", "#5B3A21"), ("gold_green", "Gold / green", "#C4A052"),
                 ("clear_blue", "Clear / blue mirror", "#B9C4CC")],
    "grill": [("gold", "Gold", "#D8B04C", 1.0), ("silver", "Silver", "#CDD0D3", 0.7), ("rose_gold", "Rose gold", "#CB917B", 1.0),
              ("iced", "Iced (stones)", "#E9EEF2", 3.0)],
    "florals": [("blue_floral", "Blue floral", "#3E5D8C"), ("red_floral", "Red floral", "#A83A3A"), ("yellow_floral", "Yellow floral", "#D9B44A"),
                ("black_floral", "Black floral", "#232325")],
}


def variants(palette, only=None):
    out = []
    for entry in PALETTES[palette]:
        vid, label, hexv = entry[0], entry[1], entry[2]
        if only and vid not in only:
            continue
        v = {"Id": vid, "Label": label, "Hex": hexv, "PriceFactor": entry[3] if len(entry) > 3 else 1.0}
        if len(entry) > 4:
            v["TrimHex"] = entry[4]
        out.append(v)
    return out


BOUTIQUE = ["clothing_boutique"]
SPORT = ["sporting_goods"]
HARDWARE = ["hardware_store"]
PHARMACY = ["pharmacy"]
CORNER = ["corner_store", "gas_station"]

FEM = ["Feminine"]
MASC = ["Masculine"]


def item(iid, label, slot, category, price, palette, sold=BOUTIQUE, tags=None, starter=False, warmth=0.3, formality=0.3,
         common=1.0, cut=None, only=None, palette2=None):
    v = variants(palette, only)
    if palette2:
        v += variants(palette2)
    return {"Id": iid, "Label": label, "Slot": slot, "Category": category, "Tags": tags or [], "Variants": v,
            "PriceCents": int(round(price * 100)), "SoldBy": sold, "Starter": starter, "Warmth": warmth,
            "Formality": formality, "Commonness": common, "Cut": cut or []}


CLOTHING = [
    # --- tops
    item("tee_crew", "Crew-neck tee", "Top", "Casual", 14, "basics", BOUTIQUE + CORNER, starter=True, warmth=0.2, formality=0.15, common=4, palette2="brights"),
    item("tee_vneck", "V-neck tee", "Top", "Casual", 16, "basics", starter=True, warmth=0.2, formality=0.2, common=1.5),
    item("tee_pocket", "Pocket tee", "Top", "Casual", 18, "basics", warmth=0.2, formality=0.2, common=1.2),
    item("tee_long", "Long-sleeve tee", "Top", "Casual", 22, "basics", starter=True, warmth=0.35, formality=0.2, common=1.5),
    item("tee_graphic", "Graphic tee (Port Arden)", "Top", "Street", 24, "basics", BOUTIQUE + SPORT, tags=["print"], warmth=0.2, formality=0.1, common=1.5),
    item("tee_oversized", "Oversized tee", "Top", "Street", 26, "basics", starter=True, warmth=0.2, formality=0.1, common=1.2),
    item("tank_athletic", "Athletic tank", "Top", "Athletic", 18, "athletic", SPORT, tags=["sleeveless"], starter=True, warmth=0.1, formality=0.05, common=0.9),
    item("tank_ribbed", "Ribbed tank", "Top", "Casual", 16, "basics", tags=["sleeveless"], warmth=0.1, formality=0.1, common=1.0),
    item("camisole", "Camisole", "Top", "Nightlife", 28, "basics", tags=["sleeveless"], warmth=0.1, formality=0.35, common=0.6, cut=FEM),
    item("crop_top", "Crop top", "Top", "Street", 22, "basics", tags=["cropped"], warmth=0.1, formality=0.15, common=0.6, cut=FEM, palette2="brights"),
    item("polo", "Polo shirt", "Top", "Casual", 34, "basics", tags=["collar"], starter=True, warmth=0.25, formality=0.45, common=1.4, palette2="brights"),
    item("oxford", "Oxford button-down", "Top", "Work", 48, "basics", tags=["collar", "buttons"], starter=True, warmth=0.3, formality=0.6, common=1.3, only=["white", "sky", "navy", "heather"]),
    item("flannel", "Flannel shirt", "Top", "Casual", 42, "brights", tags=["collar", "buttons", "plaid"], starter=True, warmth=0.45, formality=0.3, common=1.2),
    item("linen_shirt", "Linen shirt", "Top", "Casual", 58, "basics", tags=["collar", "buttons"], warmth=0.15, formality=0.45, common=0.6, only=["white", "sand", "sky", "olive"]),
    item("camp_shirt", "Camp-collar shirt", "Top", "Casual", 45, "florals", tags=["collar", "buttons", "print"], warmth=0.15, formality=0.35, common=0.6),
    item("dress_shirt", "Dress shirt", "Top", "Formal", 65, "basics", tags=["collar", "buttons", "tucked"], warmth=0.3, formality=0.85, common=0.8, only=["white", "sky", "black", "heather"]),
    item("henley", "Henley", "Top", "Casual", 32, "basics", tags=["buttons"], warmth=0.3, formality=0.3, common=0.8),
    item("sweater_crew", "Crew-neck sweater", "Top", "Casual", 60, "basics", warmth=0.65, formality=0.5, common=0.9, palette2="brights"),
    item("turtleneck", "Turtleneck", "Top", "Formal", 55, "basics", warmth=0.65, formality=0.65, common=0.4, only=["black", "heather", "navy", "sand"]),
    item("blouse", "Blouse", "Top", "Work", 52, "basics", tags=["buttons"], warmth=0.25, formality=0.65, common=1.0, cut=FEM, palette2="florals"),
    item("sports_bra", "Sports bra", "Top", "Athletic", 35, "athletic", SPORT, tags=["cropped", "sleeveless"], warmth=0.05, formality=0.0, common=0.4, cut=FEM),
    item("jersey_basketball", "Basketball jersey", "Top", "Athletic", 60, "athletic", SPORT, tags=["sleeveless", "print"], warmth=0.1, formality=0.05, common=0.4),
    item("work_shirt", "Work shirt with name patch", "Top", "Work", 30, "chino", HARDWARE, tags=["collar", "buttons", "patch"], warmth=0.35, formality=0.35, common=0.8),
    item("scrubs_top", "Scrubs top", "Top", "Uniform", 28, "scrubs", PHARMACY, warmth=0.2, formality=0.3, common=0.5),
    # --- outer
    item("hoodie", "Pullover hoodie", "Outer", "Street", 45, "basics", BOUTIQUE + SPORT, tags=["hood", "standalone"], starter=True, warmth=0.6, formality=0.1, common=2.0, palette2="brights"),
    item("zip_hoodie", "Zip hoodie", "Outer", "Street", 50, "basics", BOUTIQUE + SPORT, tags=["hood", "open_front", "standalone"], warmth=0.55, formality=0.1, common=1.2),
    item("crewneck_sweatshirt", "Crewneck sweatshirt", "Outer", "Casual", 38, "basics", tags=["standalone"], starter=True, warmth=0.55, formality=0.15, common=1.2),
    item("denim_jacket", "Denim jacket", "Outer", "Casual", 78, "denim", tags=["open_front", "collar"], starter=True, warmth=0.5, formality=0.3, common=0.9, only=["light_wash", "mid_wash", "dark_wash", "black_denim"]),
    item("bomber", "Bomber jacket", "Outer", "Street", 95, "basics", tags=["open_front"], warmth=0.55, formality=0.35, common=0.8, only=["black", "olive", "navy", "burgundy"]),
    item("leather_jacket", "Leather jacket", "Outer", "Nightlife", 260, "leather", tags=["open_front", "collar"], warmth=0.6, formality=0.45, common=0.5),
    item("varsity_jacket", "Varsity jacket", "Outer", "Street", 120, "athletic", tags=["open_front"], warmth=0.55, formality=0.2, common=0.3),
    item("windbreaker", "Windbreaker", "Outer", "Athletic", 55, "athletic", SPORT, tags=["hood", "standalone"], warmth=0.35, formality=0.1, common=0.6),
    item("rain_jacket", "Rain jacket", "Outer", "Outdoor", 85, "brights", SPORT, tags=["hood", "waterproof", "standalone"], warmth=0.4, formality=0.2, common=0.8),
    item("puffer", "Puffer jacket", "Outer", "Outdoor", 140, "basics", SPORT + BOUTIQUE, tags=["standalone"], warmth=0.9, formality=0.2, common=0.4, only=["black", "navy", "olive", "sand"]),
    item("trench", "Trench coat", "Outer", "Formal", 190, "earth", tags=["open_front", "collar", "long"], warmth=0.6, formality=0.75, common=0.3, only=["tan", "black", "wheat"]),
    item("overcoat", "Wool overcoat", "Outer", "Formal", 240, "suit", tags=["open_front", "collar", "long"], warmth=0.85, formality=0.85, common=0.2),
    item("blazer", "Blazer", "Outer", "Work", 150, "suit", tags=["open_front", "collar"], warmth=0.45, formality=0.8, common=0.7),
    item("suit_jacket", "Suit jacket", "Outer", "Formal", 280, "suit", tags=["open_front", "collar", "suit"], warmth=0.45, formality=0.95, common=0.5),
    item("cardigan", "Cardigan", "Outer", "Casual", 55, "basics", tags=["open_front", "buttons"], warmth=0.5, formality=0.45, common=0.7),
    item("fleece", "Fleece pullover", "Outer", "Outdoor", 60, "basics", SPORT, tags=["standalone"], warmth=0.7, formality=0.15, common=0.6),
    item("track_jacket", "Track jacket", "Outer", "Athletic", 65, "athletic", SPORT, tags=["open_front", "standalone"], warmth=0.4, formality=0.1, common=0.6),
    item("hivis_vest", "Hi-vis safety vest", "Outer", "Work", 15, "hivis", HARDWARE, tags=["open_front", "sleeveless"], warmth=0.05, formality=0.1, common=0.4),
    item("quilted_vest", "Quilted vest", "Outer", "Outdoor", 70, "basics", tags=["sleeveless"], warmth=0.45, formality=0.3, common=0.3, only=["black", "navy", "olive"]),
    # --- full body
    item("sundress", "Sundress", "FullBody", "Casual", 48, "florals", tags=["dress", "sleeveless"], starter=True, warmth=0.1, formality=0.35, common=0.9, cut=FEM, palette2="brights"),
    item("wrap_dress", "Wrap dress", "FullBody", "Work", 85, "basics", tags=["dress"], warmth=0.25, formality=0.65, common=0.6, cut=FEM, palette2="florals"),
    item("slip_dress", "Slip dress", "FullBody", "Nightlife", 95, "basics", tags=["dress", "sleeveless"], warmth=0.1, formality=0.7, common=0.3, cut=FEM, only=["black", "burgundy", "sand", "navy"]),
    item("cocktail_dress", "Cocktail dress", "FullBody", "Formal", 160, "brights", tags=["dress"], warmth=0.15, formality=0.9, common=0.2, cut=FEM, palette2="basics"),
    item("maxi_dress", "Maxi dress", "FullBody", "Casual", 70, "florals", tags=["dress", "long"], warmth=0.2, formality=0.45, common=0.5, cut=FEM),
    item("jumpsuit", "Jumpsuit", "FullBody", "Casual", 90, "basics", warmth=0.3, formality=0.5, common=0.3),
    item("coveralls", "Work coveralls", "FullBody", "Work", 55, "chino", HARDWARE, tags=["uniform"], warmth=0.5, formality=0.1, common=0.3),
    item("overalls", "Denim overalls", "FullBody", "Casual", 68, "denim", HARDWARE + BOUTIQUE, warmth=0.4, formality=0.15, common=0.25),
    # --- bottoms
    item("jeans_slim", "Slim jeans", "Bottom", "Casual", 60, "denim", tags=["belt_loops"], starter=True, warmth=0.45, formality=0.3, common=2.5),
    item("jeans_straight", "Straight jeans", "Bottom", "Casual", 58, "denim", tags=["belt_loops"], starter=True, warmth=0.45, formality=0.3, common=2.5),
    item("jeans_relaxed", "Relaxed jeans", "Bottom", "Casual", 55, "denim", tags=["belt_loops"], warmth=0.45, formality=0.25, common=1.5),
    item("jeans_baggy", "Baggy jeans", "Bottom", "Street", 65, "denim", tags=["belt_loops"], warmth=0.45, formality=0.15, common=1.0),
    item("jeans_skinny", "Skinny jeans", "Bottom", "Casual", 60, "denim", tags=["belt_loops"], warmth=0.45, formality=0.3, common=1.3),
    item("chinos", "Chinos", "Bottom", "Work", 55, "chino", tags=["belt_loops"], starter=True, warmth=0.4, formality=0.55, common=1.8),
    item("cargo_pants", "Cargo pants", "Bottom", "Street", 58, "chino", tags=["belt_loops"], warmth=0.45, formality=0.15, common=0.8),
    item("work_pants", "Work pants", "Bottom", "Work", 45, "chino", HARDWARE, tags=["belt_loops"], warmth=0.5, formality=0.2, common=0.9),
    item("dress_trousers", "Dress trousers", "Bottom", "Formal", 110, "suit", tags=["belt_loops", "pressed"], warmth=0.4, formality=0.85, common=0.8),
    item("suit_trousers", "Suit trousers", "Bottom", "Formal", 160, "suit", tags=["belt_loops", "pressed", "suit"], warmth=0.4, formality=0.95, common=0.4),
    item("joggers", "Joggers", "Bottom", "Athletic", 45, "basics", BOUTIQUE + SPORT, starter=True, warmth=0.45, formality=0.05, common=1.5, only=["black", "heather", "navy", "olive"]),
    item("track_pants", "Track pants", "Bottom", "Athletic", 50, "athletic", SPORT, warmth=0.4, formality=0.05, common=0.6),
    item("sweatpants", "Sweatpants", "Bottom", "Casual", 38, "basics", tags=[], warmth=0.5, formality=0.0, common=0.8, only=["heather", "black", "navy"]),
    item("shorts_athletic", "Athletic shorts", "Bottom", "Athletic", 28, "athletic", SPORT, starter=True, warmth=0.05, formality=0.0, common=1.0),
    item("shorts_chino", "Chino shorts", "Bottom", "Casual", 40, "chino", tags=["belt_loops"], starter=True, warmth=0.05, formality=0.2, common=1.3),
    item("shorts_denim", "Denim shorts", "Bottom", "Casual", 42, "denim", tags=["belt_loops"], starter=True, warmth=0.05, formality=0.15, common=1.0),
    item("shorts_cargo", "Cargo shorts", "Bottom", "Casual", 38, "chino", tags=["belt_loops"], warmth=0.05, formality=0.1, common=0.8),
    item("board_shorts", "Board shorts", "Bottom", "Swim", 45, "brights", SPORT, tags=["swim"], warmth=0.0, formality=0.0, common=0.3),
    item("skirt_pencil", "Pencil skirt", "Bottom", "Work", 60, "suit", tags=["skirt"], warmth=0.2, formality=0.8, common=0.6, cut=FEM),
    item("skirt_pleated", "Pleated midi skirt", "Bottom", "Casual", 55, "basics", tags=["skirt"], warmth=0.2, formality=0.5, common=0.5, cut=FEM),
    item("skirt_denim", "Denim skirt", "Bottom", "Casual", 45, "denim", tags=["skirt", "belt_loops"], starter=True, warmth=0.15, formality=0.25, common=0.6, cut=FEM),
    item("leggings", "Leggings", "Bottom", "Athletic", 40, "athletic", SPORT + BOUTIQUE, starter=True, warmth=0.35, formality=0.05, common=1.0, cut=FEM),
    item("scrubs_pants", "Scrubs pants", "Bottom", "Uniform", 26, "scrubs", PHARMACY, warmth=0.35, formality=0.3, common=0.5),
    # --- belts
    item("belt_leather", "Leather belt", "Belt", "Work", 35, "leather", starter=True, formality=0.6, warmth=0.0, common=2.0),
    item("belt_woven", "Woven belt", "Belt", "Casual", 30, "earth", formality=0.35, warmth=0.0, common=0.5),
    item("belt_web", "Canvas web belt", "Belt", "Casual", 18, "basics", SPORT + BOUTIQUE, formality=0.2, warmth=0.0, common=0.8, only=["black", "olive", "navy", "sand"]),
    item("belt_studded", "Studded belt", "Belt", "Nightlife", 40, "leather", formality=0.2, warmth=0.0, common=0.15, only=["black"]),
    item("belt_monogram", "Monogram buckle belt", "Belt", "Luxury", 450, "leather", tags=["luxury"], formality=0.6, warmth=0.0, common=0.05),
    item("belt_western", "Western buckle belt", "Belt", "Casual", 70, "leather", formality=0.4, warmth=0.0, common=0.2),
    # --- shoes
    item("sneakers_white", "Leather court sneakers", "Shoes", "Casual", 85, "sneaker", BOUTIQUE + SPORT, starter=True, formality=0.3, common=3.0),
    item("sneakers_canvas", "Canvas low-tops", "Shoes", "Casual", 50, "sneaker", BOUTIQUE + SPORT, starter=True, formality=0.2, common=1.8),
    item("sneakers_high", "High-top sneakers", "Shoes", "Street", 95, "sneaker", BOUTIQUE + SPORT, formality=0.15, common=1.0),
    item("running_shoes", "Running shoes", "Shoes", "Athletic", 110, "athletic", SPORT, starter=True, formality=0.1, common=1.8),
    item("basketball_shoes", "Basketball shoes", "Shoes", "Athletic", 140, "athletic", SPORT, formality=0.1, common=0.6),
    item("limited_sneakers", "Limited-run sneakers", "Shoes", "Luxury", 380, "sneaker", BOUTIQUE, tags=["luxury"], formality=0.2, common=0.05),
    item("slides", "Slides", "Shoes", "Casual", 25, "basics", SPORT + CORNER, starter=True, formality=0.0, common=0.8, only=["black", "white", "navy"]),
    item("flip_flops", "Flip-flops", "Shoes", "Swim", 10, "brights", CORNER + PHARMACY, formality=0.0, common=0.6),
    item("work_boots", "Steel-toe work boots", "Shoes", "Work", 130, "earth", HARDWARE, tags=["boots"], formality=0.15, warmth=0.5, common=1.0),
    item("chelsea_boots", "Chelsea boots", "Shoes", "Casual", 150, "leather", tags=["boots"], formality=0.6, warmth=0.4, common=0.5),
    item("combat_boots", "Combat boots", "Shoes", "Street", 120, "leather", tags=["boots"], formality=0.25, warmth=0.5, common=0.4, only=["black", "brown"]),
    item("cowboy_boots", "Cowboy boots", "Shoes", "Casual", 190, "earth", tags=["boots", "heeled"], formality=0.45, warmth=0.4, common=0.5),
    item("hiking_boots", "Hiking boots", "Shoes", "Outdoor", 140, "earth", SPORT, tags=["boots"], formality=0.1, warmth=0.5, common=0.3),
    item("loafers", "Loafers", "Shoes", "Work", 120, "leather", formality=0.7, common=0.7),
    item("oxfords", "Oxford shoes", "Shoes", "Formal", 160, "leather", formality=0.95, common=0.6, only=["black", "brown"]),
    item("pumps", "Pumps", "Shoes", "Formal", 110, "basics", tags=["heeled"], formality=0.85, common=0.5, cut=FEM, only=["black", "sand", "burgundy", "navy"]),
    item("heeled_sandals", "Strappy heeled sandals", "Shoes", "Nightlife", 95, "basics", tags=["heeled"], formality=0.7, common=0.3, cut=FEM, only=["black", "sand", "white"]),
    item("flats", "Ballet flats", "Shoes", "Casual", 60, "basics", starter=True, formality=0.5, common=0.7, cut=FEM, only=["black", "sand", "navy", "burgundy"]),
    item("rain_boots", "Rain boots", "Shoes", "Outdoor", 55, "brights", SPORT, tags=["boots", "waterproof"], formality=0.1, common=0.15),
    item("clogs", "Work clogs", "Shoes", "Uniform", 70, "basics", PHARMACY, formality=0.2, common=0.2, only=["black", "white", "navy"]),
    # --- socks
    item("socks_crew", "Crew socks", "Socks", "Casual", 8, "basics", BOUTIQUE + SPORT + PHARMACY, starter=True, warmth=0.1, formality=0.1, common=3.0, only=["white", "black", "heather"]),
    item("socks_ankle", "Ankle socks", "Socks", "Athletic", 7, "basics", SPORT + PHARMACY, starter=True, warmth=0.05, formality=0.05, common=1.5, only=["white", "black"]),
    item("socks_dress", "Dress socks", "Socks", "Formal", 12, "suit", starter=True, warmth=0.1, formality=0.8, common=1.0),
    item("socks_tall", "Tall patterned socks", "Socks", "Street", 14, "brights", warmth=0.15, formality=0.1, common=0.3),
    # --- hats
    item("cap_baseball", "Baseball cap", "Hat", "Casual", 25, "basics", BOUTIQUE + SPORT + CORNER, starter=True, formality=0.1, common=1.5, palette2="brights"),
    item("cap_snapback", "Flat-brim snapback", "Hat", "Street", 32, "athletic", BOUTIQUE + SPORT, formality=0.05, common=0.6),
    item("cap_trucker", "Trucker cap", "Hat", "Work", 18, "chino", CORNER + HARDWARE, formality=0.05, common=0.6),
    item("bucket_hat", "Bucket hat", "Hat", "Street", 28, "basics", formality=0.1, common=0.3),
    item("beanie", "Beanie", "Hat", "Casual", 20, "basics", BOUTIQUE + SPORT, starter=True, warmth=0.4, formality=0.1, common=0.7, palette2="brights"),
    item("fedora", "Fedora", "Hat", "Formal", 75, "suit", formality=0.7, common=0.1),
    item("cowboy_hat", "Cowboy hat", "Hat", "Casual", 120, "earth", formality=0.4, common=0.25),
    item("sun_hat", "Wide-brim sun hat", "Hat", "Outdoor", 35, "earth", formality=0.3, common=0.2),
    item("visor", "Sport visor", "Hat", "Athletic", 18, "athletic", SPORT, formality=0.05, common=0.15),
    item("hard_hat", "Hard hat", "Hat", "Work", 25, "hivis", HARDWARE, tags=["uniform"], formality=0.05, common=0.2),
    item("durag", "Durag", "Hat", "Street", 12, "basics", BOUTIQUE + CORNER, formality=0.05, common=0.3),
    item("headwrap", "Headwrap", "Hat", "Casual", 22, "brights", formality=0.4, common=0.25, palette2="florals"),
    item("newsboy_cap", "Newsboy cap", "Hat", "Casual", 40, "suit", formality=0.4, common=0.1),
    # --- glasses
    item("glasses_rect", "Rectangular frames", "Glasses", "Casual", 120, "sunglass", PHARMACY + BOUTIQUE, tags=["prescription"], starter=True, formality=0.4, common=1.2, only=["black", "tortoise"]),
    item("glasses_round", "Round frames", "Glasses", "Casual", 130, "sunglass", PHARMACY + BOUTIQUE, tags=["prescription"], starter=True, formality=0.45, common=0.7),
    item("glasses_cateye", "Cat-eye frames", "Glasses", "Casual", 140, "sunglass", BOUTIQUE, tags=["prescription"], formality=0.5, common=0.3, cut=FEM, only=["black", "tortoise"]),
    item("reading_glasses", "Reading glasses", "Glasses", "Casual", 20, "sunglass", PHARMACY, tags=["prescription"], formality=0.3, common=0.5, only=["black", "tortoise"]),
    item("sunglasses_aviator", "Aviator sunglasses", "Glasses", "Casual", 90, "sunglass", BOUTIQUE + CORNER, tags=["sun"], starter=True, formality=0.35, common=0.7),
    item("sunglasses_square", "Square sunglasses", "Glasses", "Casual", 80, "sunglass", BOUTIQUE + CORNER, tags=["sun"], starter=True, formality=0.35, common=0.8),
    item("sunglasses_sport", "Wraparound sport sunglasses", "Glasses", "Athletic", 110, "sunglass", SPORT, tags=["sun"], formality=0.05, common=0.3),
    item("sunglasses_oversized", "Oversized sunglasses", "Glasses", "Luxury", 240, "sunglass", BOUTIQUE, tags=["sun", "luxury"], formality=0.5, common=0.15),
    # --- masks
    item("mask_surgical", "Surgical mask", "Mask", "Casual", 2, "scrubs", PHARMACY, formality=0.2, common=0.2, only=["ceil"]),
    item("bandana_face", "Bandana (over the face)", "Mask", "Street", 8, "brights", CORNER, tags=["conceals"], formality=0.0, common=0.0),
    # --- earrings and piercings
    item("earrings_studs", "Stud earrings", "Earrings", "Casual", 45, "metal", BOUTIQUE, starter=True, formality=0.5, common=1.2),
    item("earrings_hoops_small", "Small hoops", "Earrings", "Casual", 55, "metal", BOUTIQUE, starter=True, formality=0.45, common=1.0),
    item("earrings_hoops_large", "Large hoops", "Earrings", "Nightlife", 65, "metal", BOUTIQUE, formality=0.45, common=0.5, cut=FEM),
    item("earrings_drop", "Drop earrings", "Earrings", "Formal", 120, "metal", BOUTIQUE, formality=0.8, common=0.3),
    item("earrings_iced_studs", "Iced-out studs", "Earrings", "Luxury", 900, "metal", BOUTIQUE, tags=["luxury", "stones"], formality=0.6, common=0.05),
    item("ear_gauges", "Ear gauges", "Earrings", "Street", 30, "metal", BOUTIQUE, formality=0.1, common=0.1, only=["silver", "gunmetal"]),
    item("nose_stud", "Nose stud", "FacePiercing", "Casual", 30, "metal", BOUTIQUE, starter=True, formality=0.4, common=0.4),
    item("nose_ring", "Nose ring", "FacePiercing", "Street", 30, "metal", BOUTIQUE, formality=0.3, common=0.2),
    item("septum_ring", "Septum ring", "FacePiercing", "Street", 35, "metal", BOUTIQUE, formality=0.2, common=0.15),
    item("eyebrow_bar", "Eyebrow bar", "FacePiercing", "Street", 25, "metal", BOUTIQUE, formality=0.1, common=0.05),
    item("lip_ring", "Lip ring", "FacePiercing", "Street", 25, "metal", BOUTIQUE, formality=0.1, common=0.05),
    # --- necklaces
    item("chain_thin", "Thin chain", "Necklace", "Casual", 60, "metal", BOUTIQUE, starter=True, formality=0.5, common=0.8),
    item("chain_cuban", "Cuban link chain", "Necklace", "Luxury", 1400, "metal", BOUTIQUE, tags=["luxury"], formality=0.5, common=0.08),
    item("chain_rope", "Rope chain", "Necklace", "Street", 480, "metal", BOUTIQUE, formality=0.45, common=0.1),
    item("pendant_cross", "Cross pendant", "Necklace", "Casual", 90, "metal", BOUTIQUE, formality=0.5, common=0.4),
    item("pendant_initial", "Initial pendant", "Necklace", "Casual", 75, "metal", BOUTIQUE, formality=0.5, common=0.3),
    item("pearl_strand", "Pearl strand", "Necklace", "Formal", 320, "basics", BOUTIQUE, formality=0.9, common=0.1, only=["white"]),
    item("choker", "Choker", "Necklace", "Nightlife", 25, "basics", BOUTIQUE, formality=0.4, common=0.15, only=["black"]),
    item("dog_tags", "Dog tags", "Necklace", "Street", 20, "metal", BOUTIQUE + SPORT, formality=0.1, common=0.1, only=["silver", "gunmetal"]),
    item("locket", "Locket", "Necklace", "Casual", 110, "metal", BOUTIQUE, formality=0.6, common=0.1),
    # --- grills
    item("grill_top6", "Top six grill", "Teeth", "Luxury", 600, "grill", BOUTIQUE, tags=["luxury"], formality=0.3, common=0.0),
    item("grill_full", "Full top and bottom grill", "Teeth", "Luxury", 1800, "grill", BOUTIQUE, tags=["luxury"], formality=0.3, common=0.0),
    item("grill_single_cap", "Single gold cap", "Teeth", "Street", 150, "grill", BOUTIQUE, formality=0.3, common=0.02, only=["gold", "silver"]),
    item("grill_fangs", "Fang caps", "Teeth", "Street", 250, "grill", BOUTIQUE, formality=0.2, common=0.0),
    # --- watches
    item("watch_digital", "Digital sport watch", "Watch", "Athletic", 40, "athletic", SPORT + BOUTIQUE, starter=True, formality=0.1, common=0.8, only=["black", "white", "grey"]),
    item("watch_field", "Steel field watch", "Watch", "Casual", 180, "metal", BOUTIQUE, starter=True, formality=0.5, common=0.6, only=["silver", "gunmetal"]),
    item("watch_dress_leather", "Dress watch on leather", "Watch", "Formal", 260, "leather", BOUTIQUE, formality=0.9, common=0.4),
    item("watch_dive", "Dive watch", "Watch", "Casual", 420, "metal", BOUTIQUE, formality=0.6, common=0.2, only=["silver", "gunmetal", "gold"]),
    item("watch_smart", "Smartwatch", "Watch", "Casual", 300, "athletic", BOUTIQUE + SPORT, formality=0.3, common=0.8, only=["black", "white", "grey"]),
    item("watch_gold", "Gold dress watch", "Watch", "Luxury", 6500, "metal", BOUTIQUE, tags=["luxury"], formality=0.9, common=0.03, only=["gold", "rose_gold"]),
    item("watch_chronograph", "Luxury chronograph", "Watch", "Luxury", 12000, "metal", BOUTIQUE, tags=["luxury", "stones"], formality=0.8, common=0.01),
    # --- bracelets and rings
    item("bracelet_beaded", "Beaded bracelet", "Bracelet", "Casual", 15, "earth", BOUTIQUE, starter=True, formality=0.2, common=0.5),
    item("bracelet_chain", "Chain bracelet", "Bracelet", "Casual", 70, "metal", BOUTIQUE, formality=0.5, common=0.3),
    item("bracelet_bangle", "Bangles", "Bracelet", "Casual", 40, "metal", BOUTIQUE, formality=0.5, common=0.3, cut=FEM),
    item("bracelet_tennis", "Tennis bracelet", "Bracelet", "Luxury", 2200, "metal", BOUTIQUE, tags=["luxury", "stones"], formality=0.85, common=0.02),
    item("bracelet_cuff", "Leather cuff", "Bracelet", "Street", 30, "leather", BOUTIQUE, formality=0.2, common=0.1),
    item("ring_band", "Plain band", "Rings", "Casual", 90, "metal", BOUTIQUE, starter=True, formality=0.6, common=1.0),
    item("ring_signet", "Signet ring", "Rings", "Formal", 220, "metal", BOUTIQUE, formality=0.7, common=0.2),
    item("rings_stacked", "Stacked rings", "Rings", "Casual", 80, "metal", BOUTIQUE, formality=0.4, common=0.3),
    item("ring_statement", "Statement ring", "Rings", "Luxury", 1500, "metal", BOUTIQUE, tags=["luxury", "stones"], formality=0.6, common=0.02),
    # --- gloves
    item("gloves_work", "Work gloves", "Gloves", "Work", 15, "earth", HARDWARE, formality=0.05, warmth=0.2, common=0.2),
    item("gloves_leather", "Leather gloves", "Gloves", "Formal", 60, "leather", BOUTIQUE, formality=0.8, warmth=0.5, common=0.1),
    item("gloves_knit", "Knit gloves", "Gloves", "Casual", 18, "basics", BOUTIQUE + SPORT, formality=0.2, warmth=0.6, common=0.1),
    item("gloves_fingerless", "Fingerless gloves", "Gloves", "Street", 16, "basics", BOUTIQUE, formality=0.1, warmth=0.3, common=0.05, only=["black"]),
    # --- bags
    item("backpack", "Backpack", "Bag", "Casual", 55, "basics", BOUTIQUE + SPORT, starter=True, formality=0.15, common=1.2),
    item("messenger_bag", "Messenger bag", "Bag", "Work", 70, "earth", BOUTIQUE, formality=0.45, common=0.4),
    item("tote", "Canvas tote", "Bag", "Casual", 25, "basics", BOUTIQUE + CORNER, starter=True, formality=0.3, common=0.6),
    item("crossbody", "Crossbody bag", "Bag", "Casual", 60, "leather", BOUTIQUE, formality=0.45, common=0.6),
    item("belt_bag", "Belt bag", "Bag", "Street", 35, "basics", BOUTIQUE + SPORT, formality=0.1, common=0.4),
    item("duffel", "Gym duffel", "Bag", "Athletic", 45, "athletic", SPORT, formality=0.05, common=0.3),
    item("briefcase", "Briefcase", "Bag", "Work", 180, "leather", BOUTIQUE, formality=0.85, common=0.2),
    item("handbag_designer", "Designer-style handbag", "Bag", "Luxury", 1900, "leather", BOUTIQUE, tags=["luxury"], formality=0.8, common=0.03),
]


# ----------------------------------------------------------------------------------------------- walks & animations

def walk(wid, label, speed, stride, arms, sway, bounce, lean, roll, head, min_age=0, max_age=120, common=1.0,
         ext=0.0, con=0.0, neu=0.0, agr=0.0, situational=False):
    return {"Id": wid, "Label": label, "Speed": speed, "Stride": stride, "ArmSwing": arms, "HipSway": sway,
            "Bounce": bounce, "Lean": lean, "ShoulderRoll": roll, "HeadDown": head, "MinAge": min_age,
            "MaxAge": max_age, "Commonness": common, "Extraversion": ext, "Conscientiousness": con,
            "Neuroticism": neu, "Agreeableness": agr, "Situational": situational}


WALKS = [
    walk("casual", "Casual", 1.35, 1.0, 0.5, 0.25, 0.3, 2, 0.15, 3, common=3.0),
    walk("brisk", "Brisk commuter", 1.6, 1.08, 0.55, 0.15, 0.25, 5, 0.1, 4, 18, 70, 1.4, con=1),
    walk("stroll", "Easy stroll", 1.1, 0.92, 0.4, 0.3, 0.25, 0, 0.15, 2, common=1.2, agr=1),
    walk("confident", "Confident", 1.45, 1.1, 0.6, 0.25, 0.3, -1, 0.25, -2, 16, 80, 1.0, ext=1, neu=-1),
    walk("swagger", "Swagger", 1.25, 1.05, 0.45, 0.2, 0.45, -3, 0.6, 0, 14, 45, 0.6, ext=1, con=-1),
    walk("strut", "Strut", 1.4, 1.02, 0.55, 0.7, 0.3, -1, 0.2, -2, 16, 60, 0.5, ext=1),
    walk("bouncy", "Bouncy", 1.5, 1.0, 0.65, 0.2, 0.7, 2, 0.15, 0, 5, 30, 0.7, ext=1, neu=-1),
    walk("reserved", "Reserved", 1.25, 0.9, 0.3, 0.1, 0.2, 4, 0.05, 8, 12, 90, 0.8, ext=-1, neu=1),
    walk("hurried", "Hurried", 1.75, 1.0, 0.6, 0.15, 0.3, 8, 0.1, 6, 16, 75, 0.6, neu=1, con=1),
    walk("laid_back", "Laid back", 1.15, 0.95, 0.25, 0.3, 0.25, -2, 0.3, 0, 14, 70, 0.8, con=-1, neu=-1),
    walk("upright", "Upright, measured", 1.4, 1.05, 0.5, 0.1, 0.2, -1, 0.05, -1, 20, 90, 0.4, con=1),
    walk("heavy", "Heavy step", 1.15, 0.9, 0.45, 0.35, 0.15, 3, 0.35, 3, 25, 90, 0.6),
    walk("tired", "Tired shuffle", 1.0, 0.8, 0.25, 0.15, 0.1, 6, 0.1, 10, 16, 90, 0.4, con=-1),
    walk("elderly", "Elderly", 0.85, 0.72, 0.25, 0.1, 0.1, 10, 0.05, 8, 70, 120, 3.0),
    walk("child", "Child", 1.3, 0.85, 0.7, 0.2, 0.6, 0, 0.1, 0, 0, 12, 3.0),
    # Situational: applied by state, never someone's own walk.
    walk("injured", "Limping", 0.7, 0.7, 0.3, 0.4, 0.1, 8, 0.2, 6, situational=True, common=0),
    walk("drunk", "Drunk", 1.0, 0.9, 0.6, 0.6, 0.2, 4, 0.4, 5, 18, situational=True, common=0),
    walk("cold", "Hunched against the cold", 1.5, 0.9, 0.15, 0.1, 0.15, 8, 0.3, 10, situational=True, common=0),
    walk("rain", "Hurrying out of the rain", 1.8, 0.95, 0.3, 0.1, 0.25, 9, 0.1, 12, situational=True, common=0),
    walk("carrying", "Carrying something heavy", 1.0, 0.85, 0.0, 0.2, 0.15, -4, 0.0, 4, situational=True, common=0),
    walk("phone", "Walking while texting", 1.1, 0.9, 0.1, 0.15, 0.2, 5, 0.05, 25, 12, 70, situational=True, common=0),
    walk("sneak", "Sneaking", 0.9, 0.8, 0.2, 0.1, 0.05, 12, 0.1, 5, situational=True, common=0),
]

LOCOMOTION = ["idle", "walk", "walk_start", "walk_stop", "turn_left_90", "turn_right_90", "turn_180"]

SHARED = {
    "Locomotion": ["jog", "run", "sprint", "run_stop", "jump", "fall_loop", "land_soft", "land_hard", "climb_low",
                   "vault", "crouch_idle", "crouch_walk", "strafe_left", "strafe_right", "walk_backward",
                   "stairs_up", "stairs_down", "slope_up", "slope_down"],
    "Idle": ["weight_shift", "look_around", "check_watch", "stretch", "yawn", "arms_crossed", "hands_in_pockets",
             "hands_on_hips", "lean_wall", "lean_railing", "tap_foot", "scratch_head", "rub_neck", "adjust_clothes",
             "wait_impatient", "cold_shiver", "wipe_sweat", "fix_hair"],
    "Conversation": ["talk_neutral_1", "talk_neutral_2", "talk_neutral_3", "talk_explain", "talk_excited",
                     "talk_angry", "talk_sad", "talk_flirt", "talk_whisper", "listen", "listen_skeptical", "nod",
                     "shake_head", "shrug", "laugh", "laugh_big", "point", "count_on_fingers", "argue",
                     "calm_down_gesture", "thinking", "surprised", "disgusted", "sigh"],
    "Social": ["wave", "wave_big", "handshake", "hug", "fist_bump", "high_five", "dap", "kiss_cheek", "bow",
               "salute", "beckon", "shoo_away", "pat_shoulder", "comfort", "introduce"],
    "Emote": ["cheer", "clap", "facepalm", "thumbs_up", "thumbs_down", "celebrate", "cry", "flex", "air_guitar",
              "peace_sign", "shush", "blow_kiss", "rage", "pray", "cross_fingers"],
    "Dance": ["dance_two_step", "dance_sway", "dance_club_1", "dance_club_2", "dance_hip_hop_1", "dance_hip_hop_2",
              "dance_zydeco", "dance_line", "dance_slow_couple", "dance_silly"],
    "Phone": ["phone_text", "phone_call", "phone_scroll", "phone_photo", "phone_selfie", "phone_put_away",
              "phone_video_call", "phone_walk_text"],
    "Sit": ["sit_chair", "sit_chair_idle", "sit_chair_talk", "sit_bench", "sit_bench_relaxed", "sit_ground",
            "sit_bar_stool", "sit_car_passenger", "stand_up", "lie_down", "lie_idle", "sleep"],
    "Eat": ["eat_standing", "eat_seated", "drink_cup", "drink_bottle", "drink_can", "toast_glass", "eat_sandwich"],
    "Work": ["type_laptop", "type_desk", "cash_register", "sweep", "mop", "carry_box", "stack_boxes", "hammer",
             "saw", "drill", "cook_stove", "chop", "serve_plate", "wipe_table", "write_notes", "read_book",
             "read_newspaper", "pour_drink", "cut_hair", "fold_laundry", "stock_shelves", "fix_car_under",
             "fix_car_hood", "pump_gas", "direct_traffic", "treat_patient", "write_ticket"],
    "Reaction": ["flinch", "startle", "cower", "hands_up", "panic_run", "fall_down", "get_up_front", "get_up_back",
                 "hit_front", "hit_back", "hit_left", "hit_right", "stagger", "knockdown", "look_at_explosion",
                 "cover_ears", "duck", "shield_eyes", "vomit", "cough", "sneeze", "limp_hurt", "clutch_arm"],
    "Combat": ["guard_idle", "jab", "cross", "hook", "uppercut", "kick_front", "block", "dodge_left", "dodge_right",
               "grapple", "shove", "swing_bat", "stab", "pistol_idle", "pistol_aim", "pistol_fire", "pistol_reload",
               "pistol_holster", "stun_fire", "surrender_kneel", "cuffed_idle", "arrest_cuff"],
    "Vehicle": ["car_enter_left", "car_enter_right", "car_exit_left", "car_exit_right", "car_drive_idle",
                "car_steer_left", "car_steer_right", "car_passenger_idle", "car_horn", "bike_ride", "bus_stand_hold"],
    "Swim": ["tread_water", "swim", "swim_fast", "climb_out_water", "wade"],
    "Power": ["power_cast", "power_channel", "power_blast", "hover_idle", "fly_forward", "fly_fast", "land_super",
              "teleport_in", "teleport_out", "power_overload"],
}


def animations():
    clips = []
    for w in WALKS:
        if w["Situational"]:
            clips.append({"Id": "loco_" + w["Id"] + "_walk", "Category": "Locomotion", "Label": w["Label"] + " walk",
                          "Loop": True, "WalkStyle": w["Id"], "Status": "Planned"})
            continue
        for part in LOCOMOTION:
            clips.append({"Id": "loco_" + w["Id"] + "_" + part, "Category": "Locomotion",
                          "Label": w["Label"] + " " + part.replace("_", " "), "Loop": part in ("idle", "walk"),
                          "WalkStyle": w["Id"], "Status": "Planned"})
    loops = {"Idle", "Dance", "Swim"}
    for category, names in SHARED.items():
        for n in names:
            clips.append({"Id": n, "Category": category, "Label": n.replace("_", " ").capitalize(),
                          "Loop": category in loops or n.endswith("_idle") or n.endswith("_loop") or n in ("jog", "run", "sprint", "crouch_walk", "walk_backward", "listen", "sleep", "lie_idle"),
                          "WalkStyle": "", "Status": "Planned"})
    return {"WalkStyles": WALKS, "Clips": clips}


# ----------------------------------------------------------------------------------------------- output

def outputs():
    return {
        "appearance.json": appearance(),
        "clothing.json": CLOTHING,
        "animations.json": animations(),
    }


def main():
    check = "--check" in sys.argv
    stale = []
    for name, data in outputs().items():
        path = os.path.normpath(os.path.join(DATA, name))
        text = json.dumps(data, indent=1, ensure_ascii=False) + "\n"
        if check:
            if not os.path.exists(path) or open(path, encoding="utf-8").read() != text:
                stale.append(name)
            continue
        with open(path, "w", encoding="utf-8", newline="\n") as f:
            f.write(text)
        print("wrote", path)
    if check:
        if stale:
            print("out of date:", ", ".join(stale), "- run Tools/Characters/generate_catalogs.py")
            sys.exit(1)
        print("character catalogs up to date")
    a = appearance()
    print(len(a["Morphs"]), "morphs,", len(a["HairStyles"]), "hairstyles,", len(a["FacialHair"]), "facial hair,",
          len(a["TattooDesigns"]), "tattoos,", len(CLOTHING), "clothing items (",
          sum(len(i["Variants"]) for i in CLOTHING), "colourways),", len(WALKS), "walks,", len(animations()["Clips"]), "animations")


if __name__ == "__main__":
    main()
