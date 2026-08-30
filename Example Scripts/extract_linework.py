#! python 3


import rhinoscriptsyntax as rs
import scriptcontext as sc
import math
import copy

import System
import System.Collections.Generic
import Rhino


assembly_name = "assy1"
part_name = "D01"


def get_parts_part(assembly_name, part_name):
    layer_string = "ASSEMBLY MANAGER::PARTS::" + assembly_name + "::" + part_name
    return layer_string

# will generate and produce engrave geometry and apply it to the layer
def create_engrave(assembly_name, part_name):
    parts_part_layer = get_parts_part(assembly_name, part_name)
    actual_part_layer = parts_part_layer + "::3D"
    actual_part_layer_id = rs.LayerId(actual_part_layer)
    part = rs.ObjectsByLayer(actual_part_layer_id)
    # create engrave linework
    # place engravelinework where it doesn't collide with any other features
    # create layer for engrave, move engrave to layer

# extracts exterior profile of part
def extract_exterior_profile(assembly_name, part_name):
    parts_part_layer = get_parts_part(assembly_name, part_name)
    actual_part_layer = parts_part_layer + "::3D"
    actual_part_layer_id = rs.LayerId(actual_part_layer)
    part = rs.ObjectsByLayer(actual_part_layer_id)
    # add logic to figure out what the profile curve is

