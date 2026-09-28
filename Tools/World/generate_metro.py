#!/usr/bin/env python3
"""Generates the full Port Arden metro layout (layout_port_arden.json).

The three vertical-slice districts (Eastwater, Harbor Row, Channelside) are copied verbatim from
layout_vertical_slice.json so every Story Mode place still exists. The other sixteen districts from
GAME_DESIGN_BIBLE.md section 2.3 are laid out procedurally around planned centres: an arterial street
grid, civic services, businesses built on the existing templates, the section 2.4 landmarks, and
parcel blocks sized for roughly 50,000 residents. Inter-district avenues, a freeway loop and the
Coquina Causeway tie the metro together.

Deterministic: the same script always writes the same file (seeded RNG, sorted output, no clock).

    python3 Tools/World/generate_metro.py            # write the layout
    python3 Tools/World/generate_metro.py --check    # fail if the file on disk is out of date
"""
import argparse
import json
import math
import os
import random
import sys

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
DATA = os.path.join(ROOT, "Game", "Assets", "StreamingAssets", "Data")
SLICE = os.path.join(DATA, "layout_vertical_slice.json")
OUT = os.path.join(DATA, "layout_port_arden.json")

# Default opening hours by place kind (minutes after midnight; 0/0 = always open).
HOURS = {
    "Shop": (480, 1260), "Restaurant": (420, 1380), "Office": (480, 1140), "Factory": (360, 1320),
    "Warehouse": (360, 1320), "School": (420, 1020), "University": (420, 1320), "Hospital": (0, 0),
    "PoliceStation": (0, 0), "FireStation": (0, 0), "Park": (360, 1380), "Church": (480, 1260),
    "Gym": (300, 1380), "Nightlife": (1080, 180), "Stadium": (600, 1380), "TransitStop": (0, 0),
    "GasStation": (0, 0), "Garage": (480, 1080), "Government": (480, 1020), "Beach": (360, 1320),
    "Dock": (0, 0),
}

TEMPLATE_KIND = {
    "corner_store": "Shop", "supermarket": "Shop", "pharmacy": "Shop", "clothing_boutique": "Shop",
    "hardware_store": "Shop", "barbershop": "Shop", "laundromat": "Shop", "diner": "Restaurant",
    "taqueria": "Restaurant", "coffee_shop": "Restaurant", "seafood_restaurant": "Restaurant",
    "neighborhood_bar": "Nightlife", "nightclub": "Nightlife", "auto_garage": "Garage",
    "gas_station": "GasStation", "gym": "Gym", "logistics_depot": "Warehouse", "bank_branch": "Office",
    "car_dealership": "Shop", "sporting_goods": "Shop",
}

TEMPLATE_SIZE = {  # width, depth, height, capacity
    "corner_store": (18, 16, 5, 10), "supermarket": (60, 45, 8, 200), "pharmacy": (24, 20, 5, 25),
    "clothing_boutique": (16, 18, 5, 15), "hardware_store": (40, 30, 7, 40), "barbershop": (14, 14, 5, 8),
    "laundromat": (18, 16, 5, 15), "diner": (22, 18, 5, 50), "taqueria": (18, 16, 5, 30),
    "coffee_shop": (14, 14, 5, 24), "seafood_restaurant": (28, 22, 6, 90), "neighborhood_bar": (18, 20, 6, 60),
    "nightclub": (34, 30, 9, 300), "auto_garage": (32, 24, 7, 10), "gas_station": (36, 30, 6, 12),
    "gym": (30, 24, 7, 60), "logistics_depot": (80, 50, 11, 120), "bank_branch": (24, 20, 7, 40),
    "car_dealership": (60, 40, 7, 30), "sporting_goods": (44, 32, 8, 60),
}

TEMPLATE_VALUE = {  # base property value in dollars
    "corner_store": 420000, "supermarket": 3800000, "pharmacy": 900000, "clothing_boutique": 520000,
    "hardware_store": 1600000, "barbershop": 260000, "laundromat": 300000, "diner": 480000,
    "taqueria": 460000, "coffee_shop": 380000, "seafood_restaurant": 900000, "neighborhood_bar": 520000,
    "nightclub": 1900000, "auto_garage": 720000, "gas_station": 1400000, "gym": 700000,
    "logistics_depot": 4200000, "bank_branch": 1300000, "car_dealership": 3200000, "sporting_goods": 1700000,
}

# Words for generated, original business names (no real brands).
SURNAMES = ["Arceneaux", "Batiste", "Boudreaux", "Castillo", "Chau", "Comeaux", "Dang", "Delgado", "Doucet",
            "Esposito", "Fontenot", "Garza", "Guidry", "Halvorsen", "Hebert", "Ibarra", "Jolivette",
            "Kowalczyk", "Landry", "LeBlanc", "Luong", "Mouton", "Navarro", "Okafor", "Olivier", "Pham",
            "Prejean", "Quintero", "Robichaux", "Saucier", "Soileau", "Tran", "Trahan", "Vasquez",
            "Villarreal", "Whitlock", "Yancey", "Zeringue", "Abernathy", "Babineaux", "Carrizales",
            "Dupuis", "Escobedo", "Falgout", "Gautreaux", "Hollier", "Istre", "Kingsley", "Lejeune",
            "Marcantel", "Nquyen", "Ortego", "Pitre", "Richard", "Sonnier", "Thibodeaux", "Venable", "Wiltz"]
ADJ = ["Blue", "Golden", "Salt", "Silver", "Rusty", "Lucky", "Crescent", "Gulf", "Harbor", "Bayou", "Coastal",
       "Old", "Twin", "Red", "Copper", "Cypress", "Pelican", "Heron", "Marsh", "Sandbar", "Tidal", "Brass",
       "Iron", "Magnolia", "Palmetto", "Driftwood", "Anchor", "Levee", "Southern", "Sunrise", "Northside"]
NOUN = ["Pelican", "Heron", "Crab", "Gator", "Anchor", "Lantern", "Oyster", "Mullet", "Egret", "Shrimp Boat",
        "Levee", "Tugboat", "Porch", "Kettle", "Skillet", "Compass", "Marlin", "Redfish", "Buoy", "Wharf",
        "Cane", "Pecan", "Cicada", "Firefly", "Mockingbird", "Spoonbill", "Lighthouse", "Rice Mill"]
PATTERNS = {
    "corner_store": ["{s} Corner Market", "{a} {n} Market", "{d} Food Mart", "{s} Grocery"],
    "supermarket": ["{d} Fresh Market", "{a} Basket Supermarket", "{s} Family Foods"],
    "pharmacy": ["{d} Pharmacy", "{s} Drugs", "{a} Cross Pharmacy"],
    "clothing_boutique": ["{a} Thread Boutique", "{s} & Daughters Clothiers", "The {n} Closet"],
    "hardware_store": ["{d} Hardware", "{s} Lumber & Hardware", "{a} Tool House"],
    "barbershop": ["{s}'s Barbershop", "{a} Fade Barbers", "{d} Clippers"],
    "laundromat": ["{d} Wash & Fold", "{a} Suds Laundromat", "{s} Coin Laundry"],
    "diner": ["{a} {n} Diner", "{s}'s Diner", "{d} Grill"],
    "taqueria": ["Taqueria {s}", "Tacos {a} {n}", "{d} Taqueria"],
    "coffee_shop": ["{a} {n} Coffee", "{s} Roasters", "{d} Coffee House"],
    "seafood_restaurant": ["{a} {n} Seafood", "{s}'s Oyster Bar", "{d} Boil House"],
    "neighborhood_bar": ["The {a} {n}", "{s}'s Tavern", "{d} Icehouse"],
    "nightclub": ["Club {n}", "The {a} Room", "{d} After Dark"],
    "auto_garage": ["{s} Auto Repair", "{a} {n} Garage", "{d} Tire & Lube"],
    "gas_station": ["{d} Fuel Stop", "{a} {n} Gas", "{s} Service Station"],
    "gym": ["{a} {n} Fitness", "{d} Strength Club", "{s} Boxing Gym"],
    "logistics_depot": ["{a} Freight Depot", "{s} Logistics", "{d} Distribution Center"],
    "bank_branch": ["Gulf Tidewater Bank - {d}", "Crescent Savings - {d}", "Pelican Credit Union - {d}"],
    "car_dealership": ["{s} Motors", "{a} Auto Plaza", "{d} Pre-Owned"],
    "sporting_goods": ["{d} Outfitters", "{s} Sporting Goods", "{a} {n} Outdoor Supply"],
}

STREET_WORDS = ["Acadia", "Alder", "Ambrose", "Anchorage", "Arbor", "Ashland", "Audubon", "Azalea", "Balsam",
                "Bayberry", "Beacon", "Belmont", "Birch", "Bluebonnet", "Bolivar", "Brazos", "Briar", "Brook",
                "Calcasieu", "Camellia", "Canebrake", "Cardinal", "Carrollton", "Cedar", "Chenier", "Chestnut",
                "Clearwater", "Cormorant", "Cottonwood", "Crescent", "Crestview", "Dauphine", "Delta",
                "Dogwood", "Driftwood", "Dunmore", "Egret", "Elmwood", "Esplanade", "Fairmont", "Fernwood",
                "Galvez", "Gardenia", "Glenwood", "Greenbriar", "Hackberry", "Harrow", "Hawthorne", "Hazel",
                "Heron", "Hibiscus", "Hollyhock", "Holt", "Iberia", "Indigo", "Ivy", "Jasmine", "Juniper",
                "Kestrel", "Kingfisher", "Lakeshore", "Larkspur", "Laurel", "Linden", "Live Oak", "Longleaf",
                "Lotus", "Mallard", "Maple", "Marigold", "Meadow", "Mimosa", "Mistletoe", "Moss", "Myrtle",
                "Nutmeg", "Oakdale", "Oleander", "Orchid", "Osprey", "Palmetto Grove", "Pampas", "Park Hill",
                "Pelham", "Periwinkle", "Persimmon", "Pinehurst", "Plover", "Poplar", "Primrose", "Quail",
                "Redbud", "Reed", "Ridgeway", "Rosemary", "Saffron", "Sabine", "Sandpiper", "Sassafras",
                "Seagrass", "Sequoia", "Shady Oak", "Sparrow", "Spruce", "Sugarberry", "Sumac", "Sweetgum",
                "Sycamore", "Tallow", "Teal", "Thistle", "Timberline", "Tupelo", "Verbena", "Vine", "Walnut",
                "Warbler", "Willow", "Wisteria", "Wren", "Yaupon", "Yucca", "Zinnia"]
STREET_SUFFIX = ["St", "Ave", "Ln", "Dr", "Ct", "Way", "Pl", "Rd", "Cir", "Trace", "Loop", "Row"]


def place(key, name, kind, x, z, w=20, d=20, h=8, cap=10, tags=None, prop=None, business=None, rot=0):
    open_m, close_m = HOURS.get(kind, (0, 0))
    p = {"Key": key, "Name": name, "Kind": kind, "X": round(x, 1), "Z": round(z, 1), "Width": w, "Depth": d,
         "Height": h, "RotationY": rot, "Capacity": cap, "OpenMinute": open_m, "CloseMinute": close_m,
         "Tags": tags or []}
    if prop:
        p["Property"] = prop
    if business:
        p["Business"] = business
    return p


def prop_spec(kind, zoning, floor, lot, value_dollars, bedrooms=0, floors=1, owner="npc"):
    return {"Kind": kind, "Zoning": zoning, "FloorAreaSqm": floor, "LotAreaSqm": lot, "Bedrooms": bedrooms,
            "Floors": floors, "BaseValueCents": int(value_dollars * 100), "Variation": 0.12, "Owner": owner}


# Residential parcel archetypes: spacing, footprint, heights, capacities, property.
HOUSING = {
    "shotgun": dict(Kind="Residence", SpacingX=24, SpacingZ=34, Width=10, Depth=16, MinHeight=4.5, MaxHeight=6,
                    MinCapacity=3, MaxCapacity=5, VacancyRate=0.08,
                    Property=prop_spec("House", "Residential", 95, 300, 140000, 2)),
    "ranch": dict(Kind="Residence", SpacingX=30, SpacingZ=40, Width=16, Depth=14, MinHeight=4.5, MaxHeight=6,
                  MinCapacity=3, MaxCapacity=5, VacancyRate=0.04,
                  Property=prop_spec("House", "Residential", 150, 700, 240000, 3)),
    "subdivision": dict(Kind="Residence", SpacingX=32, SpacingZ=42, Width=18, Depth=16, MinHeight=6, MaxHeight=8,
                        MinCapacity=3, MaxCapacity=6, VacancyRate=0.05,
                        Property=prop_spec("House", "Residential", 210, 750, 330000, 4, 2)),
    "rowhouse": dict(Kind="Residence", SpacingX=12, SpacingZ=36, Width=9, Depth=14, MinHeight=8, MaxHeight=11,
                     MinCapacity=2, MaxCapacity=4, VacancyRate=0.05,
                     Property=prop_spec("House", "Residential", 140, 160, 360000, 2, 3)),
    "beach_house": dict(Kind="Residence", SpacingX=34, SpacingZ=46, Width=14, Depth=16, MinHeight=7, MaxHeight=9,
                        MinCapacity=2, MaxCapacity=5, VacancyRate=0.12,
                        Property=prop_spec("House", "Residential", 170, 600, 520000, 3, 2)),
    "mansion": dict(Kind="Residence", SpacingX=70, SpacingZ=80, Width=32, Depth=26, MinHeight=9, MaxHeight=13,
                    MinCapacity=3, MaxCapacity=6, VacancyRate=0.03,
                    Property=prop_spec("Mansion", "Residential", 650, 4200, 2600000, 6, 3)),
    "estate": dict(Kind="Residence", SpacingX=50, SpacingZ=60, Width=24, Depth=22, MinHeight=8, MaxHeight=11,
                   MinCapacity=3, MaxCapacity=6, VacancyRate=0.03,
                   Property=prop_spec("House", "Residential", 380, 2000, 1250000, 5, 2)),
    "farmstead": dict(Kind="Residence", SpacingX=160, SpacingZ=180, Width=16, Depth=14, MinHeight=5, MaxHeight=7,
                      MinCapacity=3, MaxCapacity=6, VacancyRate=0.06,
                      Property=prop_spec("House", "Agricultural", 160, 60000, 380000, 3)),
    "fishing_camp": dict(Kind="Residence", SpacingX=60, SpacingZ=70, Width=10, Depth=12, MinHeight=4, MaxHeight=5,
                         MinCapacity=1, MaxCapacity=4, VacancyRate=0.15,
                         Property=prop_spec("House", "Residential", 70, 1500, 90000, 2)),
    "fourplex": dict(Kind="ApartmentBuilding", SpacingX=36, SpacingZ=40, Width=18, Depth=20, MinHeight=7, MaxHeight=9,
                     MinCapacity=8, MaxCapacity=12, VacancyRate=0.06,
                     Property=prop_spec("Apartment", "Residential", 420, 700, 520000, 0, 2)),
    "garden_apartments": dict(Kind="ApartmentBuilding", SpacingX=60, SpacingZ=60, Width=40, Depth=24, MinHeight=9,
                              MaxHeight=12, MinCapacity=40, MaxCapacity=70, VacancyRate=0.05,
                              Property=prop_spec("Apartment", "Residential", 2400, 3000, 3200000, 0, 3)),
    "midrise": dict(Kind="ApartmentBuilding", SpacingX=60, SpacingZ=60, Width=40, Depth=36, MinHeight=18,
                    MaxHeight=30, MinCapacity=90, MaxCapacity=160, VacancyRate=0.04,
                    Property=prop_spec("Apartment", "MixedUse", 6200, 2200, 11000000, 0, 7)),
    "highrise": dict(Kind="ApartmentBuilding", SpacingX=75, SpacingZ=75, Width=40, Depth=40, MinHeight=60,
                     MaxHeight=140, MinCapacity=220, MaxCapacity=380, VacancyRate=0.03,
                     Property=prop_spec("Condo", "MixedUse", 21000, 2600, 52000000, 0, 30)),
    "student_housing": dict(Kind="ApartmentBuilding", SpacingX=60, SpacingZ=60, Width=44, Depth=26, MinHeight=12,
                            MaxHeight=18, MinCapacity=60, MaxCapacity=110, VacancyRate=0.04,
                            Property=prop_spec("Apartment", "Residential", 3600, 2400, 6400000, 0, 4)),
    # Workplace parcels.
    "offices": dict(Kind="Office", SpacingX=70, SpacingZ=70, Width=44, Depth=40, MinHeight=30, MaxHeight=110,
                    MinCapacity=200, MaxCapacity=700, VacancyRate=0.08,
                    Property=prop_spec("Commercial", "Commercial", 14000, 2800, 36000000, 0, 18)),
    "warehouses": dict(Kind="Warehouse", SpacingX=110, SpacingZ=80, Width=64, Depth=44, MinHeight=9, MaxHeight=13,
                       MinCapacity=20, MaxCapacity=70, VacancyRate=0.12,
                       Property=prop_spec("Warehouse", "Industrial", 2600, 5400, 1400000, 0, 1)),
    "factories": dict(Kind="Factory", SpacingX=140, SpacingZ=110, Width=90, Depth=60, MinHeight=12, MaxHeight=20,
                      MinCapacity=60, MaxCapacity=220, VacancyRate=0.1,
                      Property=prop_spec("Industrial", "Industrial", 5200, 11000, 4800000, 0, 1)),
    "farms": dict(Kind="Factory", SpacingX=240, SpacingZ=220, Width=40, Depth=30, MinHeight=6, MaxHeight=10,
                  MinCapacity=4, MaxCapacity=14, VacancyRate=0.05,
                  Property=prop_spec("Land", "Agricultural", 900, 120000, 650000, 0, 1)),
}


def district(key, name, dtype, wealth, crime, foot, trust, flood, cx, cz, radius, grid, description,
             avenue, cross, cells, landmarks, services, businesses):
    return dict(key=key, name=name, type=dtype, wealth=wealth, crime=crime, foot=foot, trust=trust, flood=flood,
                cx=cx, cz=cz, r=radius, grid=grid, description=description, avenue=avenue, cross=cross,
                cells=cells, landmarks=landmarks, services=services, businesses=businesses)


# Landmark tuples: (key, name, kind, width, depth, height, capacity, tags)
DISTRICTS = [
    district("arden_central", "Arden Central", "Downtown", 0.72, 0.28, 1.9, 0.58, 0.3, 1500, 700, 460, 230,
             "The skyline: Meridian Tower over Founders' Square, City Hall, hotels, museums and the transit hub. "
             "Busy by day, spotty by night.", "Founders Avenue", "Commerce Street",
             [("highrise", 6), ("midrise", 6), ("offices", 4)],
             [("ac_meridian", "Meridian Tower", "Office", 70, 70, 260, 2600, ["landmark", "meridian"]),
              ("ac_square", "Founders' Square", "Park", 110, 90, 0, 600, ["landmark"]),
              ("ac_cityhall", "Port Arden City Hall", "Government", 80, 60, 32, 700, ["landmark", "city_hall"]),
              ("ac_station", "Arden Central Station", "TransitStop", 90, 50, 18, 900, ["landmark", "transit_hub"]),
              ("ac_museum", "Gulf Coast Museum of History", "Government", 70, 50, 20, 400, ["museum"]),
              ("ac_artmuseum", "Port Arden Museum of Art", "Government", 60, 50, 18, 300, ["museum"]),
              ("ac_grandhotel", "The Grand Tidewater Hotel", "Office", 50, 40, 80, 500, ["hotel"]),
              ("ac_harborlight", "Harborlight Hotel & Suites", "Office", 44, 36, 60, 320, ["hotel"]),
              ("ac_convention", "Arden Convention Center", "Government", 120, 80, 22, 3000, []),
              ("ac_library", "Port Arden Central Library", "Government", 60, 44, 16, 400, ["library"]),
              ("ac_courthouse", "Arden County Courthouse", "Government", 70, 50, 26, 500, ["courthouse"]),
              ("ac_hq", "PAPD Headquarters", "PoliceStation", 60, 50, 30, 400, ["police_hq"])],
             ["fire", "school", "church", "transit"],
             ["coffee_shop", "coffee_shop", "diner", "seafood_restaurant", "clothing_boutique", "clothing_boutique",
              "pharmacy", "bank_branch", "nightclub", "neighborhood_bar", "gym", "barbershop", "corner_store",
              "supermarket", "taqueria"]),
    district("the_ledger", "The Ledger", "Downtown", 0.86, 0.14, 1.2, 0.7, 0.25, 2400, 700, 320, 220,
             "Banks, trading floors and law firms in glass towers. Packed at nine, empty by seven.",
             "Exchange Boulevard", "Treasury Street",
             [("offices", 5), ("highrise", 1)],
             [("tl_exchange", "Gulf Commodities Exchange", "Office", 60, 50, 90, 900, ["landmark"]),
              ("tl_fedbuilding", "Federal Building", "Government", 60, 50, 40, 600, []),
              ("tl_reserve", "Crescent Savings Tower", "Office", 44, 44, 140, 1200, []),
              ("tl_barclub", "Arden Bar Association", "Office", 30, 24, 20, 80, [])],
             ["transit"],
             ["bank_branch", "bank_branch", "coffee_shop", "coffee_shop", "diner", "seafood_restaurant",
              "neighborhood_bar", "gym", "clothing_boutique"]),
    district("cotton_row", "Cotton Row", "Downtown", 0.6, 0.24, 1.4, 0.6, 0.45, 900, 900, 300, 200,
             "Nineteenth-century brick warehouses turned galleries and restaurants. Tourists by day, preservation "
             "fights at every council meeting.", "Old Levee Street", "Factor's Walk",
             [("rowhouse", 5), ("midrise", 1)],
             [("cr_exchange", "Old Cotton Exchange Museum", "Government", 50, 40, 18, 250, ["landmark", "museum"]),
              ("cr_square", "Factor's Square", "Park", 60, 50, 0, 200, []),
              ("cr_theater", "The Levee Street Theater", "Nightlife", 40, 50, 18, 800, ["theater"]),
              ("cr_preservation", "Cotton Row Preservation Society", "Office", 20, 18, 10, 20, [])],
             ["church", "transit"],
             ["seafood_restaurant", "seafood_restaurant", "coffee_shop", "clothing_boutique", "clothing_boutique",
              "neighborhood_bar", "neighborhood_bar", "diner", "corner_store"]),
    district("southgate_flats", "Southgate Flats", "LowIncome", 0.2, 0.5, 0.95, 0.35, 0.5, -1200, -600, 520, 240,
             "Dense older housing and struggling commercial strips, held together by strong churches and mutual aid.",
             "Southgate Boulevard", "Freedmen Avenue",
             [("shotgun", 18), ("fourplex", 8), ("garden_apartments", 5)],
             [("sg_mutualaid", "Southgate Mutual Aid Hall", "Government", 36, 28, 8, 150, ["mutual_aid"]),
              ("sg_greaterzion", "Greater Zion Baptist Church", "Church", 40, 44, 16, 700, []),
              ("sg_clinic", "Southgate Community Clinic", "Hospital", 40, 30, 10, 120, ["clinic"]),
              ("sg_rec", "Southgate Recreation Center", "Gym", 44, 36, 9, 150, [])],
             ["police", "fire", "school", "school", "park", "church", "transit"],
             ["corner_store", "corner_store", "laundromat", "laundromat", "barbershop", "barbershop", "taqueria",
              "diner", "pharmacy", "auto_garage", "gas_station", "neighborhood_bar", "supermarket"]),
    district("port_of_arden", "Port of Arden", "Port", 0.4, 0.33, 0.5, 0.5, 0.7, 1400, -1400, 600, 280,
             "Container terminal, cranes, customs and rail yards: 24-hour logistics on the ship channel.",
             "Terminal Road", "Customs Way",
             [("warehouses", 5), ("factories", 1), ("fourplex", 1)],
             [("pa_terminal", "Port of Arden Container Terminal", "Dock", 240, 120, 45, 900, ["landmark", "port"]),
              ("pa_bulk", "Port of Arden Bulk Terminal", "Dock", 160, 90, 30, 300, ["port"]),
              ("pa_customs", "Port Arden Customs House", "Government", 50, 40, 14, 200, []),
              ("pa_card", "CARD Field Office", "Government", 40, 30, 12, 80, ["card"]),
              ("pa_railyard", "Arden Southern Rail Yard", "Warehouse", 200, 60, 8, 150, []),
              ("pa_harbormaster", "Harbormaster's Office", "Government", 24, 20, 12, 40, []),
              ("pa_seamens", "Seafarers' Center", "Church", 24, 20, 8, 60, [])],
             ["police", "fire", "transit"],
             ["logistics_depot", "logistics_depot", "logistics_depot", "diner", "gas_station", "gas_station",
              "auto_garage", "neighborhood_bar", "taqueria"]),
    district("fairhaven", "Fairhaven", "Suburban", 0.5, 0.14, 0.9, 0.62, 0.3, -1500, 900, 520, 260,
             "1960s ranch houses, strip malls, good schools and ball fields.", "Fairhaven Parkway",
             "Kennedy Road",
             [("ranch", 26), ("garden_apartments", 4)],
             [("fh_mall", "Fairhaven Plaza", "Shop", 90, 50, 8, 400, ["strip_mall"]),
              ("fh_fields", "Fairhaven Ball Fields", "Park", 120, 90, 0, 400, []),
              ("fh_library", "Fairhaven Branch Library", "Government", 30, 24, 8, 120, ["library"])],
             ["police", "fire", "school", "school", "park", "church", "church", "transit"],
             ["sporting_goods", "supermarket", "pharmacy", "hardware_store", "diner", "coffee_shop", "taqueria", "gym", "barbershop",
              "laundromat", "gas_station", "gas_station", "auto_garage", "neighborhood_bar", "bank_branch"]),
    district("pinecrest_hollow", "Pinecrest Hollow", "Suburban", 0.6, 0.1, 0.8, 0.68, 0.2, -2800, 1800, 620, 300,
             "Newer subdivisions, megachurches, big-box retail and a long commute.", "Pinecrest Parkway",
             "Hollow Creek Road",
             [("subdivision", 28), ("garden_apartments", 4)],
             [("ph_megachurch", "Living Waters Fellowship", "Church", 90, 70, 20, 3000, ["megachurch"]),
              ("ph_bigbox", "Pinecrest Crossing", "Shop", 120, 80, 10, 900, ["big_box"]),
              ("ph_sports", "Pinecrest Sportsplex", "Park", 140, 100, 0, 500, [])],
             ["police", "fire", "school", "school", "school", "park", "church", "transit"],
             ["sporting_goods", "supermarket", "supermarket", "hardware_store", "pharmacy", "coffee_shop", "diner", "gym", "gym",
              "gas_station", "gas_station", "auto_garage", "car_dealership", "bank_branch", "taqueria"]),
    district("oak_terrace", "Oak Terrace", "Wealthy", 0.95, 0.05, 0.6, 0.8, 0.15, 600, 2000, 420, 260,
             "Old-money mansions under live oaks, private clubs and quiet, well-patrolled streets.",
             "Terrace Drive", "Live Oak Boulevard",
             [("mansion", 4), ("estate", 2)],
             [("ot_club", "Live Oak Country Club", "Restaurant", 70, 50, 10, 200, ["private_club"]),
              ("ot_academy", "St. Brendan's Academy", "School", 80, 60, 12, 600, ["private_school"]),
              ("ot_gardens", "Terrace Botanical Gardens", "Park", 120, 90, 0, 300, [])],
             ["police", "church"],
             ["coffee_shop", "clothing_boutique", "seafood_restaurant", "pharmacy", "gym"]),
    district("riverbend_estates", "Riverbend Estates", "Wealthy", 0.9, 0.06, 0.5, 0.75, 0.35, 1800, 2600, 460, 280,
             "Gated new money on the river: security patrols, golf and three-car garages.", "Riverbend Drive",
             "Fairway Lane",
             [("estate", 8), ("subdivision", 3)],
             [("rb_golf", "Riverbend Golf Club", "Park", 220, 160, 0, 300, ["golf"]),
              ("rb_gate", "Riverbend Security Gatehouse", "Government", 16, 12, 6, 10, ["gated"]),
              ("rb_clubhouse", "Riverbend Clubhouse", "Restaurant", 44, 32, 9, 180, ["private_club"])],
             ["fire", "school", "church"],
             ["coffee_shop", "gym", "car_dealership", "pharmacy", "seafood_restaurant"]),
    district("mercer_heights", "Mercer Heights", "Suburban", 0.45, 0.24, 1.3, 0.55, 0.2, 3200, 1600, 520, 250,
             "Tarrow State University, student housing, cheap bars and the research labs of the Halvorsen Institute.",
             "University Avenue", "Mercer Street",
             [("student_housing", 7), ("fourplex", 5), ("ranch", 6)],
             [("mh_tarrow", "Tarrow State University", "University", 260, 180, 24, 6000, ["landmark", "university"]),
              ("mh_halvorsen", "Halvorsen Institute", "University", 90, 70, 30, 700, ["landmark", "research"]),
              ("mh_stadium", "Tarrow State Field House", "Stadium", 90, 70, 18, 6000, []),
              ("mh_quad", "Mercer Quad", "Park", 100, 80, 0, 500, [])],
             ["police", "fire", "church", "transit", "transit"],
             ["coffee_shop", "coffee_shop", "coffee_shop", "neighborhood_bar", "neighborhood_bar", "neighborhood_bar",
              "nightclub", "taqueria", "diner", "laundromat", "corner_store", "pharmacy", "gym", "barbershop"]),
    district("lantana_park", "Lantana Park", "Suburban", 0.45, 0.22, 1.1, 0.55, 0.3, 3300, 0, 460, 260,
             "The stadium district: The Breakwater, Riggers Park and fan bars that fill up on game days.",
             "Stadium Boulevard", "Lantana Avenue",
             [("ranch", 14), ("garden_apartments", 3)],
             [("lp_breakwater", "The Breakwater", "Stadium", 260, 220, 45, 40000, ["landmark", "football"]),
              ("lp_riggers", "Riggers Park", "Stadium", 200, 200, 30, 25000, ["landmark", "baseball"]),
              ("lp_plaza", "Lantana Fan Plaza", "Park", 90, 60, 0, 1500, []),
              ("lp_arena", "Lantana Community Arena", "Gym", 60, 50, 14, 800, [])],
             ["police", "fire", "school", "transit", "transit"],
             ["sporting_goods", "neighborhood_bar", "neighborhood_bar", "neighborhood_bar", "nightclub", "diner", "taqueria",
              "gas_station", "corner_store", "seafood_restaurant", "clothing_boutique"]),
    district("westmarch_commons", "Westmarch Commons", "Suburban", 0.65, 0.16, 1.2, 0.62, 0.2, -3200, 200, 460, 260,
             "The shopping district: the Westmarch Commons mall, an outlet strip and auto row.",
             "Westmarch Boulevard", "Outlet Parkway",
             [("subdivision", 14), ("garden_apartments", 3)],
             [("wm_mall", "Westmarch Commons Mall", "Shop", 220, 140, 18, 4000, ["landmark", "mall"]),
              ("wm_outlets", "Westmarch Outlet Strip", "Shop", 160, 50, 8, 800, ["outlets"]),
              ("wm_cinema", "Westmarch 16 Cinemas", "Nightlife", 70, 50, 14, 900, ["cinema"])],
             ["police", "fire", "school", "park", "church", "transit"],
             ["sporting_goods", "car_dealership", "car_dealership", "car_dealership", "car_dealership", "supermarket", "clothing_boutique",
              "clothing_boutique", "coffee_shop", "diner", "gym", "hardware_store", "gas_station", "bank_branch",
              "auto_garage", "pharmacy"]),
    district("coquina_key", "Coquina Key", "Coastal", 0.7, 0.18, 1.3, 0.6, 0.9, 4500, -2500, 700, 300,
             "The barrier island: beaches, the Boardwalk and Pleasure Pier, beach houses, marinas and fishing.",
             "Seawall Boulevard", "Island Drive",
             [("beach_house", 16), ("midrise", 2)],
             [("ck_boardwalk", "Coquina Key Boardwalk", "Beach", 300, 40, 4, 3000, ["landmark", "boardwalk"]),
              ("ck_pier", "Pleasure Pier", "Nightlife", 60, 220, 12, 2500, ["landmark", "pier"]),
              ("ck_beach", "Coquina Public Beach", "Beach", 500, 80, 0, 5000, ["beach"]),
              ("ck_eastbeach", "East Beach", "Beach", 400, 80, 0, 3000, ["beach"]),
              ("ck_marina", "Coquina Key Marina", "Dock", 120, 90, 6, 300, ["marina"]),
              ("ck_fishpier", "Old Fishing Pier", "Dock", 20, 160, 4, 120, ["fishing"]),
              ("ck_hotel", "Seaspray Resort Hotel", "Office", 70, 50, 50, 600, ["hotel"]),
              ("ck_lifeguard", "Coquina Beach Patrol", "FireStation", 20, 16, 8, 30, ["lifeguard"])],
             ["police", "fire", "school", "church", "transit"],
             ["seafood_restaurant", "seafood_restaurant", "seafood_restaurant", "coffee_shop", "neighborhood_bar",
              "neighborhood_bar", "nightclub", "clothing_boutique", "corner_store", "corner_store", "gas_station",
              "diner", "taqueria"]),
    district("bitterwater_marsh", "Bitterwater Marsh", "Rural", 0.25, 0.3, 0.3, 0.45, 0.95, -800, -2400, 700, 350,
             "Bayous, boat ramps and a bird sanctuary, with fishing camps on stilts and a dumping problem nobody "
             "wants to talk about.", "Marsh Road", "Bitterwater Bayou Road",
             [("fishing_camp", 4)],
             [("bw_sanctuary", "Bitterwater Bird Sanctuary", "Park", 300, 240, 0, 200, ["sanctuary"]),
              ("bw_ramp", "Pelican Point Boat Ramp", "Dock", 40, 60, 2, 60, ["boat_ramp"]),
              ("bw_ramp2", "Heron Bayou Boat Ramp", "Dock", 40, 60, 2, 40, ["boat_ramp"]),
              ("bw_dump", "Old Shell Road Dump Site", "Vacant", 120, 90, 0, 0, ["illegal_dumping"]),
              ("bw_bait", "Bitterwater Bait & Tackle", "Shop", 16, 14, 5, 10, [])],
             ["fire", "church"],
             ["corner_store", "gas_station", "seafood_restaurant", "neighborhood_bar"]),
    district("tarrow_prairie", "Tarrow Prairie", "Rural", 0.35, 0.12, 0.3, 0.6, 0.35, -5000, -800, 900, 450,
             "Ranches and rice farms along county roads, and the little town of Tarrow with its courthouse square.",
             "County Road 12", "Rice Mill Road",
             [("farmstead", 6), ("farms", 4), ("ranch", 2)],
             [("tp_courthouse", "Tarrow Town Hall", "Government", 40, 30, 14, 120, ["town"]),
              ("tp_mill", "Tarrow Rice Mill", "Factory", 80, 50, 22, 90, []),
              ("tp_coop", "Tarrow Farmers' Co-op", "Shop", 50, 30, 8, 60, []),
              ("tp_fairgrounds", "Tarrow County Fairgrounds", "Park", 200, 160, 0, 2000, ["fairgrounds"]),
              ("tp_rodeo", "Tarrow Rodeo Arena", "Stadium", 90, 70, 12, 3000, [])],
             ["police", "fire", "school", "church", "church"],
             ["sporting_goods", "corner_store", "diner", "gas_station", "gas_station", "auto_garage", "hardware_store",
              "neighborhood_bar"]),
    district("kessler_field", "Kessler Field", "Industrial", 0.4, 0.2, 0.7, 0.6, 0.25, -4200, 2800, 620, 300,
             "The regional airport, air cargo sheds and general-aviation hangars on the metro's northern edge.",
             "Airport Boulevard", "Hangar Road",
             [("warehouses", 4), ("fourplex", 4)],
             [("kf_terminal", "Kessler Field Terminal", "TransitStop", 180, 70, 20, 2500, ["landmark", "airport"]),
              ("kf_tower", "Kessler Field Control Tower", "Government", 16, 16, 45, 40, ["airport"]),
              ("kf_cargo", "Kessler Air Cargo Center", "Warehouse", 160, 80, 14, 300, ["airport"]),
              ("kf_hangars", "General Aviation Hangars", "Garage", 140, 60, 12, 60, ["airport"]),
              ("kf_rescue", "Kessler Field Crash Rescue", "FireStation", 40, 30, 10, 40, ["airport"]),
              ("kf_hotel", "Runway Inn", "Office", 40, 30, 20, 120, ["hotel"])],
             ["police", "transit"],
             ["gas_station", "gas_station", "diner", "coffee_shop", "logistics_depot", "logistics_depot",
              "auto_garage", "car_dealership"]),
]

# Inter-district connectors: (name, kind, width, [district keys or (x, z) points]).
CONNECTORS = [
    ("Gulf Coast Freeway", "highway", 24, [(-5600, -900), "westmarch_commons", "fairhaven", (-360, 300),
                                           (1100, 300), "arden_central", "the_ledger", "lantana_park", (4200, -1200)]),
    ("Coquina North Bridge", "bridge", 20, [(4200, -1200), "coquina_key"]),
    ("Arden Loop", "highway", 22, ["kessler_field", "pinecrest_hollow", "fairhaven", "southgate_flats",
                                   "bitterwater_marsh", "port_of_arden", "lantana_park", "mercer_heights",
                                   "riverbend_estates", "oak_terrace", "pinecrest_hollow"]),
    ("Harbor Parkway", "avenue", 16, [(900, 40), "arden_central"]),
    ("Cotton Row Avenue", "avenue", 16, [(560, 240), "cotton_row", "arden_central"]),
    ("Levee Road", "avenue", 16, [(-360, -200), "southgate_flats"]),
    ("Ship Channel Road", "avenue", 16, [(620, -700), "port_of_arden"]),
    ("Oak Terrace Boulevard", "avenue", 16, ["cotton_row", "oak_terrace", "riverbend_estates"]),
    ("Northgate Avenue", "avenue", 16, ["arden_central", "riverbend_estates"]),
    ("Tarrow Highway", "highway", 20, ["tarrow_prairie", "westmarch_commons", "kessler_field"]),
    ("Prairie Road", "avenue", 14, ["tarrow_prairie", "southgate_flats"]),
    ("Ledger Avenue", "avenue", 16, ["the_ledger", "mercer_heights"]),
    ("Coquina Causeway", "bridge", 18, ["port_of_arden", (3200, -2200), "coquina_key"]),
    ("Bayou Vista Road", "avenue", 14, [(-330, 240), "fairhaven"]),
]

# District grid extents (lo_x, hi_x, lo_z, hi_z), filled in as districts are laid out; water is fitted around them.
EXTENTS = {}


def water_bodies():
    """
    Convex polygons (x/z corners). The coast runs below the southernmost district; Coquina Key is an island, cut
    off by a sound and two passes fitted to its street grid; the ship canal (vertical slice), the ship channel
    east of the port and a river north of the suburbs. Only docks, beaches and bridges may touch water.
    """
    margin = 60
    coast = min(e[2] for e in EXTENTS.values()) - margin
    ck = EXTENTS["coquina_key"]
    west, east, north = ck[0] - margin, ck[1] + margin, ck[3] + margin
    port = EXTENTS["port_of_arden"]
    river = max(e[3] for k, e in EXTENTS.items() if k != "kessler_field") + margin
    return [
        ("The Gulf", "Gulf", [(-9000, coast), (9000, coast), (9000, coast - 6000), (-9000, coast - 6000)]),
        ("Coquina Sound", "Sound", [(west - 250, north), (east + 250, north), (east + 250, north + 250), (west - 250, north + 250)]),
        ("Coquina West Pass", "Sound", [(west - 250, coast), (west, coast), (west, north), (west - 250, north)]),
        ("Coquina East Pass", "Sound", [(east, coast), (east + 250, coast), (east + 250, north), (east, north)]),
        ("Arden Ship Canal", "ShipCanal", [(-460, -875), (700, -875), (700, -785), (-460, -785)]),
        # The channel runs up from the Gulf to the middle of the port, where ships berth.
        ("Arden Ship Channel", "Channel", [(port[1] + margin, coast), (port[1] + margin + 220, coast), (port[1] + margin + 220, (port[2] + port[3]) / 2), (port[1] + margin, (port[2] + port[3]) / 2)]),
        ("Arden River", "River", [(-3400, river), (4500, river), (4500, river + 130), (-3400, river + 130)]),
    ]

CENTERS = {}


def pt(p):
    if isinstance(p, str):
        return CENTERS[p]
    return p


class Names:
    def __init__(self, taken):
        self.taken = set(taken)

    def claim(self, name):
        if name in self.taken:
            raise ValueError("duplicate name " + name)
        self.taken.add(name)
        return name

    def unique(self, rng, candidates):
        for _ in range(200):
            n = rng.choice(candidates)()
            if n not in self.taken:
                return self.claim(n)
        raise RuntimeError("could not find a unique name")


def business_name(rng, names, template, dname):
    def make(pattern):
        return lambda: pattern.format(s=rng.choice(SURNAMES), a=rng.choice(ADJ), n=rng.choice(NOUN), d=dname)
    return names.unique(rng, [make(p) for p in PATTERNS[template]])


def service_places(kind, d, names, idx):
    dn = d["name"]
    table = {
        "police": ("PoliceStation", ["PAPD {d} Precinct", "PAPD {d} Substation"], 50, 40, 12, 120),
        "fire": ("FireStation", ["Port Arden Fire Rescue Station {n}"], 40, 30, 10, 40),
        "school": ("School", ["{d} Elementary School", "{d} Middle School", "{d} High School"], 90, 60, 11, 800),
        "park": ("Park", ["{d} Park", "{d} Commons Green"], 90, 70, 0, 200),
        "church": ("Church", ["{d} First Methodist", "St. Anne's of {d}", "{d} Community Church"], 26, 32, 13, 250),
        "transit": ("TransitStop", ["{d} Transit Center", "{d} Station"], 30, 14, 5, 80),
    }
    kind_name, patterns, w, dd, h, cap = table[kind]
    for pattern in patterns:
        name = pattern.format(d=dn, n=FIRE_STATION[0])
        if name not in names.taken:
            if kind == "fire":
                FIRE_STATION[0] += 1
            return kind_name, names.claim(name), w, dd, h, cap
    raise RuntimeError("out of names for " + kind + " in " + dn)


FIRE_STATION = [12]

EVERYDAY = {
    "Downtown": ["coffee_shop", "diner", "corner_store", "pharmacy", "neighborhood_bar", "clothing_boutique",
                 "barbershop", "taqueria", "gym", "seafood_restaurant", "laundromat"],
    "LowIncome": ["corner_store", "laundromat", "barbershop", "taqueria", "diner", "auto_garage", "neighborhood_bar",
                  "gas_station", "pharmacy"],
    "Suburban": ["supermarket", "coffee_shop", "diner", "pharmacy", "gas_station", "auto_garage", "gym",
                 "barbershop", "taqueria", "hardware_store", "neighborhood_bar", "laundromat"],
    "Wealthy": ["coffee_shop", "clothing_boutique", "seafood_restaurant", "gym", "pharmacy"],
    "Port": ["logistics_depot", "diner", "gas_station", "auto_garage", "neighborhood_bar"],
    "Industrial": ["logistics_depot", "diner", "gas_station", "auto_garage"],
    "Coastal": ["seafood_restaurant", "coffee_shop", "neighborhood_bar", "corner_store", "clothing_boutique",
                "taqueria", "diner"],
    "Rural": ["corner_store", "gas_station", "diner", "auto_garage", "hardware_store"],
}


def street_names(rng, names, count):
    out = []
    words = list(STREET_WORDS)
    while len(out) < count:
        n = rng.choice(words) + " " + rng.choice(STREET_SUFFIX)
        if n not in names.taken:
            names.claim(n)
            out.append(n)
    return out


def build_district(d, names, rng, roads):
    cx, cz, r, g = d["cx"], d["cz"], d["r"], d["grid"]
    setback = 14
    inner = g - 2 * setback

    # Landmarks, services and businesses become "lots" packed into the central cells.
    lots = []
    for key, name, kind, w, dd, h, cap, tags in d["landmarks"]:
        lots.append(dict(key=key, name=names.claim(name), kind=kind, w=w, d=dd, h=h, cap=cap, tags=tags))
    for n, s in enumerate(d["services"]):
        kind, name, w, dd, h, cap = service_places(s, d, names, n)
        lots.append(dict(key="%s_%s%d" % (d["key"], s, n), name=name, kind=kind, w=w, d=dd, h=h, cap=cap, tags=[]))
    # Neighbourhood businesses scale with the amount of housing and work the district holds.
    extra = sum(count for _, count in d["cells"]) // 2
    pool = EVERYDAY.get(d["type"], EVERYDAY["Suburban"])
    businesses = list(d["businesses"]) + [pool[(i * 7 + len(d["key"])) % len(pool)] for i in range(extra)]
    for n, t in enumerate(businesses):
        name = business_name(rng, names, t, d["name"])
        w, dd, h, cap = TEMPLATE_SIZE[t]
        lots.append(dict(key="%s_biz%02d" % (d["key"], n), name=name, kind=TEMPLATE_KIND[t], w=w, d=dd, h=h,
                         cap=cap, tags=[], template=t))

    # Shelf-pack lots into cells (rows along x, wrapping in z); records (cell index, local x, local z).
    packed = []
    cell, cur_x, cur_z, row_h = 0, 0, 0, 0
    for lot in lots:
        w, dd = min(lot["w"], inner), min(lot["d"], inner)
        if cur_x + w > inner:
            cur_x, cur_z, row_h = 0, cur_z + row_h + 8, 0
        if cur_z + dd > inner:
            cell, cur_x, cur_z, row_h = cell + 1, 0, 0, 0
        packed.append((lot, cell, cur_x + w / 2, cur_z + dd / 2, w, dd))
        cur_x += w + 8
        row_h = max(row_h, dd)
    lot_cells = cell + 1

    quota = []
    for archetype, count in d["cells"]:
        quota += [archetype] * count

    # Grid just big enough for the lots and the housing mix (and at least the planned radius).
    half = max(1, int(r * 0.85 // g))
    while (2 * half) ** 2 < lot_cells + len(quota):
        half += 1
    xs = [cx + i * g for i in range(-half, half + 1)]
    zs = [cz + i * g for i in range(-half, half + 1)]
    lo_x, hi_x, lo_z, hi_z = xs[0], xs[-1], zs[0], zs[-1]
    EXTENTS[d["key"]] = (lo_x, hi_x, lo_z, hi_z)
    arterial_names = street_names(rng, names, len(xs) + len(zs))
    # Main avenue (east-west) and cross street (north-south) through the centre; the rest are streets.
    for i, z in enumerate(zs):
        main = z == cz
        roads.append({"Name": d["avenue"] if main else arterial_names[i], "Width": 16 if main else 10,
                      "Kind": "avenue" if main else "street", "Points": [lo_x, z, hi_x, z]})
    for i, x in enumerate(xs):
        main = x == cx
        roads.append({"Name": d["cross"] if main else arterial_names[len(zs) + i], "Width": 16 if main else 10,
                      "Kind": "avenue" if main else "street", "Points": [x, lo_z, x, hi_z]})

    # Cells between grid lines, nearest the centre first (ties broken by position for stability).
    cells = []
    for i in range(len(xs) - 1):
        for j in range(len(zs) - 1):
            mx, mz = (xs[i] + xs[i + 1]) / 2, (zs[j] + zs[j + 1]) / 2
            cells.append((round(math.hypot(mx - cx, mz - cz), 3), i, j, xs[i], zs[j]))
    cells.sort()

    places, blocks = [], []
    for lot, ci, lx, lz, w, dd in packed:
        c = cells[ci]
        prop = None
        business = None
        if "template" in lot:
            t = lot["template"]
            prop = prop_spec("Commercial", "Commercial", int(w * dd * 0.9), int(w * dd * 2),
                             TEMPLATE_VALUE[t] * (0.6 + d["wealth"]))
            business = {"TemplateId": t, "Name": lot["name"], "Owner": "npc", "Staff": -1}
        places.append(place(lot["key"], lot["name"], lot["kind"], c[3] + setback + lx, c[4] + setback + lz, w, dd,
                            lot["h"], lot["cap"], lot["tags"], prop, business))

    # Remaining cells: parcel blocks per the district's housing/work mix, interleaved in a stable shuffled order.
    remaining = cells[lot_cells:]
    block_streets = street_names(rng, names, len(quota))
    order = list(range(len(quota)))
    rng.shuffle(order)
    for n, qi in enumerate(order):
        archetype = quota[qi]
        spec = HOUSING[archetype]
        c = remaining[n]
        cols = min(24, max(1, int(inner // spec["SpacingX"])))  # <= 24 keeps street numbers unique per row
        rows = max(1, int(inner // spec["SpacingZ"]))
        blocks.append({"KeyPrefix": "%s_%s%d" % (d["key"], archetype, n), "Kind": spec["Kind"],
                       "OriginX": c[3] + setback + spec["Width"] / 2, "OriginZ": c[4] + setback + spec["Depth"] / 2,
                       "Rows": rows, "Columns": cols, "SpacingX": spec["SpacingX"], "SpacingZ": spec["SpacingZ"],
                       "Width": spec["Width"], "Depth": spec["Depth"], "MinHeight": spec["MinHeight"],
                       "MaxHeight": spec["MaxHeight"], "MinCapacity": spec["MinCapacity"],
                       "MaxCapacity": spec["MaxCapacity"], "VacancyRate": spec["VacancyRate"],
                       "StreetNames": [block_streets[n]], "Property": spec["Property"]})

    radius = max(r, int(half * g * 1.2))
    return {"Key": d["key"], "Name": d["name"], "Type": d["type"], "Wealth": d["wealth"],
            "CrimeBaseline": d["crime"], "FootTraffic": d["foot"], "PoliceTrust": d["trust"],
            "FloodRisk": d["flood"], "CenterX": cx, "CenterZ": cz, "Radius": radius,
            "Description": d["description"], "Places": places, "Blocks": blocks}


def generate():
    with open(SLICE, encoding="utf-8") as f:
        slice_layout = json.load(f)
    taken = set(r["Name"] for r in slice_layout["Roads"])
    for dl in slice_layout["Districts"]:
        taken.add(dl["Name"])
        for p in dl["Places"]:
            taken.add(p["Name"])
        for b in dl.get("Blocks", []):
            taken.update(b.get("StreetNames", []))
    names = Names(taken)
    for d in DISTRICTS:
        names.claim(d["avenue"])
        names.claim(d["cross"])
    for c in CONNECTORS:
        names.claim(c[0])

    rng = random.Random(20300507)
    districts = list(slice_layout["Districts"])
    roads = list(slice_layout["Roads"])
    for d in DISTRICTS:
        CENTERS[d["key"]] = (d["cx"], d["cz"])
    for dl in slice_layout["Districts"]:
        CENTERS[dl["Key"]] = (dl["CenterX"], dl["CenterZ"])
    for d in DISTRICTS:
        districts.append(build_district(d, names, rng, roads))
    for name, kind, width, points in CONNECTORS:
        flat = []
        for p in points:
            x, z = pt(p)
            flat += [x, z]
        roads.append({"Name": name, "Width": width, "Kind": kind, "Points": flat})

    for dl in slice_layout["Districts"]:
        EXTENTS.setdefault(dl["Key"], (dl["CenterX"] - dl["Radius"], dl["CenterX"] + dl["Radius"], dl["CenterZ"] - dl["Radius"], dl["CenterZ"] + dl["Radius"]))
    water = [{"Name": n, "Kind": k, "Points": [v for xz in pts for v in xz]} for n, k, pts in water_bodies()]
    layout = {"Id": "port_arden", "DisplayName": "Port Arden - Full Metro (19 districts)", "Districts": districts,
              "Roads": roads, "Water": water}
    check_water(layout)
    return layout


def in_poly(pts, x, z):
    n = len(pts) // 2
    inside = False
    j = n - 1
    for i in range(n):
        xi, zi, xj, zj = pts[2 * i], pts[2 * i + 1], pts[2 * j], pts[2 * j + 1]
        if (zi > z) != (zj > z) and x < (xj - xi) * (z - zi) / (zj - zi) + xi:
            inside = not inside
        j = i
    return inside


def check_water(layout):
    """Same rules as ContentLoader.Validate, so a bad plan fails here with a readable message."""
    water = [w["Points"] for w in layout["Water"]]
    wet = lambda x, z: any(in_poly(p, x, z) for p in water)
    problems = []
    for d in layout["Districts"]:
        for p in d["Places"]:
            if p["Kind"] not in ("Dock", "Beach") and wet(p["X"], p["Z"]):
                problems.append("%s: %s is in the water" % (d["Key"], p["Name"]))
        for b in d.get("Blocks", []):
            for r in {0, b["Rows"] - 1}:
                for c in {0, b["Columns"] - 1}:
                    if wet(b["OriginX"] + c * b["SpacingX"], b["OriginZ"] + r * b["SpacingZ"]):
                        problems.append("%s: block %s reaches into the water" % (d["Key"], b["KeyPrefix"]))
    for road in layout["Roads"]:
        if road["Kind"] == "bridge":
            continue
        pts = road["Points"]
        for i in range(0, len(pts) - 3, 2):
            if any(wet(pts[i] + (pts[i + 2] - pts[i]) * t / 8, pts[i + 1] + (pts[i + 3] - pts[i + 1]) * t / 8) for t in range(9)):
                problems.append("road %s crosses water" % road["Name"])
                break
    if problems:
        raise RuntimeError("water check failed:\n  " + "\n  ".join(sorted(set(problems))))


def render(layout):
    return json.dumps(layout, indent=1, ensure_ascii=False) + "\n"


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--check", action="store_true", help="fail if the generated file is out of date")
    args = ap.parse_args()
    text = render(generate())
    if args.check:
        with open(OUT, encoding="utf-8") as f:
            if f.read() != text:
                print("layout_port_arden.json is out of date; run Tools/World/generate_metro.py")
                return 1
        print("layout_port_arden.json is up to date")
        return 0
    with open(OUT, "w", encoding="utf-8", newline="\n") as f:
        f.write(text)
    layout = json.loads(text)
    places = sum(len(d["Places"]) for d in layout["Districts"])
    parcels = sum(b["Rows"] * b["Columns"] for d in layout["Districts"] for b in d["Blocks"])
    print("wrote %s: %d districts, %d places, %d parcels, %d roads" % (
        os.path.relpath(OUT, ROOT), len(layout["Districts"]), places, parcels, len(layout["Roads"])))
    return 0


if __name__ == "__main__":
    sys.exit(main())
