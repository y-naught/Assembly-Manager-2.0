#! python 3


import rhinoscriptsyntax as rs
import scriptcontext as sc

import System
import System.Collections.Generic
import Rhino

selection_filter = rs.filter.surface + rs.filter.polysurface
selection_filter_no_extrusion = 60



def runner():
    # gets objects from user
    split_objects = rs.GetObjects(
        message="Select objects you want to split", 
        filter=selection_filter, 
        group=True, 
        preselect=True, 
        select=True
    )
    
    plane_points = rs.GetPoints(
        draw_lines=False,
        in_plane=False,
        message1="Pick your first point",
        message2="Pick your next point",
        max_points=3
    )

    plane = rs.PlaneFromPoints(plane_points[0], plane_points[1], plane_points[2])
    plane_srf = rs.AddPlaneSurface(plane, 1000.0, 1000.0)
    area_centroid = rs.SurfaceAreaCentroid(plane_srf)
    move_vector = rs.VectorCreate(plane_points[0], area_centroid[0])
    rs.MoveObject(plane_srf, move_vector)

    new_breps = []
    for brep in split_objects:
        target_layer = rs.ObjectLayer(brep)
        split_geometry = rs.SplitBrep(brep, plane_srf, delete_input=True)
        
        print("target_layer : ", target_layer)
        for geometry in split_geometry:
            success = rs.CapPlanarHoles(geometry)
            rs.ObjectLayer(geometry, target_layer)
            if(success):
                print("Successfully capped object")
            else:
                print("Object did not have closed planar loop to cap")

    
    rs.DeleteObject(plane_srf)

if __name__=="__main__":
    runner()