#
# LabelParts.py is designed to take a component and identify all unique parts, 
# assign them to layers, and update an information model with
# the details of each part. 
#

import rhinoscriptsyntax as rs

# Enumerated Color Set

# List of variables
component_name = "FRAME_01"
label_prefix = "F"
cur_suffix = 0
parts = []
OG = []

# Get Polysurfaces from User
OG = rs.GetObjects(message="Select Objects In Component", filter=16 , select=True, preselect=True)

dims = []

# assuming world Axis here are point numbers we need to get our dims
# X = 1 - 0
# Y = 3 - 0
# Z = 4 - 0
for part in OG:
    cur_box = rs.BoundingBox(part)
    if cur_box:
        tempX = rs.Distance(cur_box[1], cur_box[0])
        tempY = rs.Distance(cur_box[3] , cur_box[0])
        tempZ = rs.Distance(cur_box[4], cur_box[0])
        temp_dims = [tempX, tempY, tempZ]
        dims.append(temp_dims)

def construct_part(dims):
    temp_suf = cur_suffix
    global cur_suffix
    cur_suffix +=1
    label = label_prefix + str(temp_suf)
    temp_qty = 1
    part = {"label" : label, "dims" : dims, "qty" : temp_qty}
    print(part)
    return part

# Clean up floating point values
for dim in dims:
    round_dim = [round(dim[0], 3),round(dim[1], 3), round(dim[2], 3)]
    sorted_dims = sorted(round_dim)
    if parts:
        found_equal = False
        for part in parts:
            sameX = part.dims[0] == sorted_dims[0]
            sameY = part.dims[1] == sorted_dims[1]
            sameZ = part.dims[2] == sorted_dims[2]
            if sameX and sameY and sameZ:
                part.qty +=1
                found_equal = True
        if not found_equal:
            parts.append(construct_part(sorted_dims))
    else:
        parts.append(construct_part(sorted_dims))

for part in parts:
    print(part)

