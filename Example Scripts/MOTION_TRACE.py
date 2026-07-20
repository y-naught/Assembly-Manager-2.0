import rhinoscriptsyntax as rs
import scriptcontext as sc
import Rhino
import System
from System.Drawing import Color
import time


def ensure_layer(full_path, color):
    parts = full_path.split('::')
    parent = None
    current_path = ''
    for part in parts:
        if current_path == '':
            current_path = part
        else:
            current_path = current_path + '::' + part
        if not rs.IsLayer(current_path):
            try:
                rs.AddLayer(part, color, True, False, parent)
            except:
                try:
                    rs.AddLayer(current_path, color)
                except:
                    pass
        parent = current_path
    return current_path


def layer_index_from_path(full_path):
    layer_id = rs.LayerId(full_path)
    if layer_id:
        for i in range(sc.doc.Layers.Count):
            if sc.doc.Layers[i].Id == layer_id:
                return i
    try:
        return sc.doc.Layers.FindByFullPath(full_path, -1)
    except:
        try:
            return sc.doc.Layers.Find(full_path, True)
        except:
            return -1


def get_or_create_hidden_linetype_index():
    names = ['Hidden', 'Dashed', 'Center']
    for name in names:
        try:
            idx = sc.doc.Linetypes.Find(name, True)
            if idx >= 0:
                return idx
        except:
            pass
    try:
        lt = Rhino.DocObjects.Linetype()
        lt.Name = 'ANNO_Hidden_Dashed'
        lt.AppendSegment(0.25, True)
        lt.AppendSegment(0.125, False)
        idx = sc.doc.Linetypes.Add(lt)
        return idx
    except:
        return -1


def set_layer_linetype(full_path, linetype_index):
    if linetype_index < 0:
        return
    idx = layer_index_from_path(full_path)
    if idx < 0:
        return
    try:
        layer = sc.doc.Layers[idx]
        layer.LinetypeIndex = linetype_index
        layer.CommitChanges()
    except:
        pass


def get_object_display_color(object_id):
    fallback = Color.FromArgb(120, 120, 120)
    robj = rs.coercerhinoobject(object_id)
    if not robj:
        return fallback
    try:
        return robj.Attributes.DrawColor(sc.doc)
    except:
        pass
    try:
        layer = sc.doc.Layers[robj.Attributes.LayerIndex]
        return layer.Color
    except:
        pass
    return fallback


def make_curve_attributes(layer_path, linetype_index, plot_weight, object_color):
    attr = Rhino.DocObjects.ObjectAttributes()
    idx = layer_index_from_path(layer_path)
    if idx >= 0:
        attr.LayerIndex = idx

    try:
        attr.ColorSource = Rhino.DocObjects.ObjectColorSource.ColorFromObject
        attr.ObjectColor = object_color
    except:
        pass

    if linetype_index >= 0:
        try:
            attr.LinetypeSource = Rhino.DocObjects.ObjectLinetypeSource.LinetypeFromObject
            attr.LinetypeIndex = linetype_index
        except:
            pass

    try:
        attr.PlotWeightSource = Rhino.DocObjects.ObjectPlotWeightSource.PlotWeightFromObject
        attr.PlotWeight = plot_weight
    except:
        pass

    return attr


def duplicate_object_geometry(object_id):
    robj = rs.coercerhinoobject(object_id)
    if not robj:
        return None
    geom = robj.Geometry
    if not geom:
        return None
    try:
        return geom.Duplicate()
    except:
        return geom


def bbox_corners_from_geometry(geom):
    corners = []
    if not geom:
        return corners
    try:
        bbox = geom.GetBoundingBox(True)
        if bbox and bbox.IsValid:
            for pt in bbox.GetCorners():
                corners.append(pt)
    except:
        pass
    return corners


def bbox_edge_curves_from_geometry(geom):
    curves = []
    corners = bbox_corners_from_geometry(geom)
    if len(corners) != 8:
        return curves
    pairs = [(0,1), (1,2), (2,3), (3,0), (4,5), (5,6), (6,7), (7,4), (0,4), (1,5), (2,6), (3,7)]
    for pair in pairs:
        try:
            line = Rhino.Geometry.Line(corners[pair[0]], corners[pair[1]])
            if line.IsValid and line.Length > sc.doc.ModelAbsoluteTolerance:
                curves.append(Rhino.Geometry.LineCurve(line))
        except:
            pass
    return curves


def curves_from_mesh_edges(mesh):
    curves = []
    try:
        topo = mesh.TopologyEdges
        for i in range(topo.Count):
            line = topo.EdgeLine(i)
            if line.IsValid and line.Length > sc.doc.ModelAbsoluteTolerance:
                curves.append(Rhino.Geometry.LineCurve(line))
    except:
        pass
    return curves


def curves_from_brep_edges(brep):
    curves = []
    if not brep:
        return curves
    try:
        edge_curves = brep.DuplicateEdgeCurves(False)
    except:
        try:
            edge_curves = brep.DuplicateEdgeCurves()
        except:
            edge_curves = []
    if edge_curves:
        for crv in edge_curves:
            if crv and crv.IsValid:
                curves.append(crv)
    return curves


def edge_curves_from_geometry(geom):
    curves = []
    if not geom:
        return curves

    try:
        if isinstance(geom, Rhino.Geometry.Curve):
            dup = geom.DuplicateCurve()
            if dup and dup.IsValid:
                curves.append(dup)
            return curves
    except:
        pass

    try:
        if isinstance(geom, Rhino.Geometry.Brep):
            curves = curves_from_brep_edges(geom)
            if curves:
                return curves
    except:
        pass

    try:
        if isinstance(geom, Rhino.Geometry.Mesh):
            curves = curves_from_mesh_edges(geom)
            if curves:
                return curves
    except:
        pass

    try:
        if hasattr(geom, 'ToBrep'):
            brep = geom.ToBrep()
            curves = curves_from_brep_edges(brep)
            if curves:
                return curves
    except:
        pass

    try:
        if hasattr(geom, 'ToMesh'):
            mesh = geom.ToMesh()
            curves = curves_from_mesh_edges(mesh)
            if curves:
                return curves
    except:
        pass

    return bbox_edge_curves_from_geometry(geom)


def add_curves_to_doc(curves, layer_path, linetype_index, plot_weight, object_color):
    added = []
    attr = make_curve_attributes(layer_path, linetype_index, plot_weight, object_color)
    for crv in curves:
        if crv and crv.IsValid:
            try:
                gid = sc.doc.Objects.AddCurve(crv, attr)
                if gid != System.Guid.Empty:
                    added.append(gid)
            except:
                try:
                    gid = sc.doc.Objects.AddCurve(crv)
                    if gid != System.Guid.Empty:
                        rs.ObjectLayer(gid, layer_path)
                        rs.ObjectColor(gid, object_color)
                        added.append(gid)
                except:
                    pass
    return added


def point_key(pt, tol):
    if tol <= 0:
        tol = 0.001
    return (int(round(pt.X / tol)), int(round(pt.Y / tol)), int(round(pt.Z / tol)))


def add_line_connector(pt_a, pt_b, layer_path, linetype_index, plot_weight, object_color):
    tol = sc.doc.ModelAbsoluteTolerance
    try:
        if pt_a.DistanceTo(pt_b) <= tol:
            return None
        line = Rhino.Geometry.Line(pt_a, pt_b)
        if not line.IsValid:
            return None
        attr = make_curve_attributes(layer_path, linetype_index, plot_weight, object_color)
        gid = sc.doc.Objects.AddCurve(Rhino.Geometry.LineCurve(line), attr)
        if gid != System.Guid.Empty:
            return gid
    except:
        try:
            gid = rs.AddLine((pt_a.X, pt_a.Y, pt_a.Z), (pt_b.X, pt_b.Y, pt_b.Z))
            if gid:
                rs.ObjectLayer(gid, layer_path)
                rs.ObjectColor(gid, object_color)
                return gid
        except:
            pass
    return None


def add_bbox_connectors(start_geom, final_geom, layer_path, linetype_index, plot_weight, used_keys, object_color):
    added = []
    tol = max(sc.doc.ModelAbsoluteTolerance * 10.0, 0.001)
    start_corners = bbox_corners_from_geometry(start_geom)
    final_corners = bbox_corners_from_geometry(final_geom)
    if len(start_corners) == 8 and len(final_corners) == 8:
        for i in range(8):
            key = (point_key(start_corners[i], tol), point_key(final_corners[i], tol))
            if key not in used_keys:
                used_keys[key] = True
                gid = add_line_connector(start_corners[i], final_corners[i], layer_path, linetype_index, plot_weight, object_color)
                if gid:
                    added.append(gid)
    return added


def add_edge_endpoint_connectors(start_curves, final_curves, layer_path, linetype_index, plot_weight, used_keys, max_connectors, object_color):
    added = []
    tol = max(sc.doc.ModelAbsoluteTolerance * 10.0, 0.001)
    if len(start_curves) != len(final_curves):
        return added
    estimated = len(start_curves) * 2
    if estimated > max_connectors:
        return added
    for i in range(len(start_curves)):
        crv_a = start_curves[i]
        crv_b = final_curves[i]
        try:
            pairs = [(crv_a.PointAtStart, crv_b.PointAtStart), (crv_a.PointAtEnd, crv_b.PointAtEnd)]
            for pair in pairs:
                key = (point_key(pair[0], tol), point_key(pair[1], tol))
                if key not in used_keys:
                    used_keys[key] = True
                    gid = add_line_connector(pair[0], pair[1], layer_path, linetype_index, plot_weight, object_color)
                    if gid:
                        added.append(gid)
        except:
            pass
    return added


def create_group_with_objects(group_name, object_ids):
    clean_ids = []
    for obj_id in object_ids:
        if obj_id:
            clean_ids.append(obj_id)
    if not clean_ids:
        return None

    final_name = group_name
    try:
        if rs.IsGroup(final_name):
            final_name = group_name + '_' + time.strftime('%H%M%S')
    except:
        pass

    try:
        grp = rs.AddGroup(final_name)
        rs.AddObjectsToGroup(clean_ids, grp)
        return grp
    except:
        return None


def main():
    objs = rs.GetObjects('Select objects to move and create hidden motion-trace linework', 0, True, True, True)
    if not objs:
        print 'No objects selected.'
        return

    start_geometries = []
    object_colors = []
    for obj in objs:
        start_geometries.append(duplicate_object_geometry(obj))
        object_colors.append(get_object_display_color(obj))

    rs.EnableRedraw(True)
    rs.UnselectAllObjects()
    rs.SelectObjects(objs)
    print 'Use the Move command prompts to place the selected objects. Pick a base point, then the new position. Press Enter or cancel to stop.'

    command_result = rs.Command('_Move _Pause _Pause', True)
    if not command_result:
        print 'Move was cancelled. No motion trace was created.'
        return

    final_geometries = []
    for obj in objs:
        final_geometries.append(duplicate_object_geometry(obj))

    stamp = time.strftime('%Y%m%d_%H%M%S')
    ensure_layer('ANNO', Color.FromArgb(80, 80, 80))
    trace_layer = ensure_layer('ANNO::MOVE_TRACE_' + stamp, Color.FromArgb(110, 110, 110))
    start_layer = ensure_layer(trace_layer + '::START_EDGES_HIDDEN', Color.FromArgb(130, 130, 130))
    final_layer = ensure_layer(trace_layer + '::FINAL_EDGES_HIDDEN', Color.FromArgb(90, 90, 90))
    motion_layer = ensure_layer(trace_layer + '::MOTION_CONNECTORS_HIDDEN', Color.FromArgb(70, 130, 190))

    hidden_lt = get_or_create_hidden_linetype_index()
    set_layer_linetype(start_layer, hidden_lt)
    set_layer_linetype(final_layer, hidden_lt)
    set_layer_linetype(motion_layer, hidden_lt)

    rs.EnableRedraw(False)

    all_added = []
    start_curves_by_object = []
    final_curves_by_object = []
    per_object_output = []

    for i in range(len(objs)):
        object_output = []
        obj_color = object_colors[i]

        start_curves = edge_curves_from_geometry(start_geometries[i])
        final_curves = edge_curves_from_geometry(final_geometries[i])
        start_curves_by_object.append(start_curves)
        final_curves_by_object.append(final_curves)

        start_added = add_curves_to_doc(start_curves, start_layer, hidden_lt, 0.18, obj_color)
        final_added = add_curves_to_doc(final_curves, final_layer, hidden_lt, 0.13, obj_color)

        object_output.extend(start_added)
        object_output.extend(final_added)
        all_added.extend(start_added)
        all_added.extend(final_added)
        per_object_output.append(object_output)

    used_connector_keys = {}
    max_edge_endpoint_connectors_per_object = 80
    for i in range(len(objs)):
        obj_color = object_colors[i]
        connectors = add_edge_endpoint_connectors(start_curves_by_object[i], final_curves_by_object[i], motion_layer, hidden_lt, 0.18, used_connector_keys, max_edge_endpoint_connectors_per_object, obj_color)
        if len(connectors) < 2:
            connectors.extend(add_bbox_connectors(start_geometries[i], final_geometries[i], motion_layer, hidden_lt, 0.18, used_connector_keys, obj_color))
        all_added.extend(connectors)
        if i < len(per_object_output):
            per_object_output[i].extend(connectors)

    # Create one master group containing all generated trace curves.
    master_group_name = 'ANNO_MOVE_TRACE_' + stamp
    create_group_with_objects(master_group_name, all_added)

    # If several separate source objects were traced, also group each object's generated curves
    # so the trace can be selected either as one overall annotation or by individual object trail.
    if len(per_object_output) > 1:
        for i in range(len(per_object_output)):
            create_group_with_objects(master_group_name + '_OBJ_' + str(i + 1), per_object_output[i])

    rs.UnselectAllObjects()
    if all_added:
        rs.SelectObjects(all_added)

    rs.EnableRedraw(True)
    sc.doc.Views.Redraw()
    print 'Created color-matched grouped motion trace on layer: ' + trace_layer
    print 'Master output group: ' + master_group_name

main()
