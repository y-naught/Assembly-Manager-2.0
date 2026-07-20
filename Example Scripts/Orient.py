"""
NOTE:

- Reference to RhinoCommmon.dll is added by default

- You can specify your script requirements like:

    # r: <package-specifier> [, <package-specifier>]
    # requirements: <package-specifier> [, <package-specifier>]

    For example this line will ask the runtime to install
    the listed packages before running the script:

    # requirements: pytoml, keras

    You can install specific versions of a package
    using pip-like package specifiers:

    # r: pytoml==0.10.2, keras>=2.6.0

- Use env directive to add an environment path to sys.path automatically
    # env: /path/to/your/site-packages/
"""
#! python3

import rhinoscriptsyntax as rs
import scriptcontext as sc
import math

import System
import System.Collections.Generic
import Rhino

# Gets all objects you want to orient to the world XY Plane
def get_user_objects():
    objects = rs.GetObjects("Select Objects to Orient", filter=0, preselect=True)
    return objects

# a helper function that takes the overate of a list of points and returns a new point
def average_points(point_list):
    computed_point = [0,0,0]
    for point in point_list:
        tmp_pt = rs.coerce3dpoint(point)
        computed_point[0] = computed_point[0] + tmp_pt[0]
        computed_point[1] = computed_point[1] + tmp_pt[1]
        computed_point[2] = computed_point[2] + tmp_pt[2]
    computed_point[0] = computed_point[0]  / len(point_list)
    computed_point[1] = computed_point[1]  / len(point_list)
    computed_point[2] = computed_point[2]  / len(point_list)
    new_point = rs.CreatePoint(computed_point)
    return new_point


# Get's the volume centroid of a list of objects
def analyze_group(objects):
    centroids = []
    for obj in objects:
        centroids.append(rs.SurfaceVolumeCentroid(obj)[0])
    return average_points(centroids)

# Finds the center of the rotation axix for a list of objects
def get_rotation_axis(objects):
    bounding_points = rs.BoundingBox(objects)
    new_point = average_points(bounding_points[0:4])
    for point in bounding_points:
        coerced_point = rs.coerce3dpoint(point)
    return new_point

# gets the bounding box volume of a list of objects
def get_bounding_volume(objects):
    bounding_points = rs.BoundingBox(objects)
    bounding_box = rs.AddBox(bounding_points)
    volume = rs.SurfaceVolume(bounding_box)
    rs.DeleteObject(bounding_box)
    return volume

# function that iteratively rotates the objects until it finds the lowest point of the gradient based on volume
# 
def optimize_volume(objects, rotation_point):
    angle_increment = 30.0
    direction = False
    tolerance = 1
    last_volume = 0
    cur_volume = get_bounding_volume(objects)[0]
    while(abs(last_volume - cur_volume) > tolerance):
        last_volume = cur_volume
        if(direction):
            rs.RotateObjects(objects, rotation_point, angle_increment, axis=None, copy=False)
        else:
            rs.RotateObjects(objects, rotation_point, angle_increment * -1.0, axis=None, copy=False)
        cur_volume = get_bounding_volume(objects)[0]
        if(cur_volume > last_volume):
            direction = not direction
            angle_increment = angle_increment / 2.0
        



# main function for runner
if __name__ == "__main__":
    objects = get_user_objects()
    rotation_point = get_rotation_axis(objects)
    optimize_volume(objects, rotation_point)