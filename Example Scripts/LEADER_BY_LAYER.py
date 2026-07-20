import rhinoscriptsyntax as rs
import scriptcontext as sc
import Rhino
import System
import math
from System.Drawing import Color

# ------------------------------------------------------------
# Layout Detail Layer Leader Tool
# ------------------------------------------------------------
# This version:
# 1. Keeps the leader in PAGE SPACE.
# 2. Finds the model object under the clicked layout detail point.
# 3. Uses the CURRENT document annotation/dimension style.
# 4. Does NOT create a new annotation style.
# 5. Sets the leader text orientation behavior back to Horizontal, not Aligned.
# 6. Applies the 90-degree rotation correction while preserving the final leader point locations.
#
# The point-preserving rotation is done by:
# - pre-rotating the picked leader points by -90 degrees around the final text point,
# - creating the leader from those temporary points,
# - setting horizontal text behavior,
# - rotating the whole leader +90 degrees around the same final text point.
# Result: final visible leader points return to the user's picked locations.
# ------------------------------------------------------------

LABEL_USES_FULL_LAYER_PATH = False
LEADER_LAYER_SHORT_NAME = "LEADERS"
PICK_TOLERANCE_PIXELS = 24.0
MAX_LEADER_POINTS = 20
CURVE_SAMPLE_COUNT = 96
BREP_EDGE_SAMPLE_COUNT = 20
LEADER_CORRECTION_ROTATION_DEGREES = 90.0


def get_active_page_view():
    view = sc.doc.Views.ActiveView
    if view is None:
        return None
    try:
        if view.GetType().Name == "RhinoPageView":
            return view
    except:
        pass
    return None


def set_page_as_active(page_view):
    if page_view is None:
        return False
    try:
        page_view.SetPageAsActive()
        sc.doc.Views.Redraw()
        return True
    except:
        pass
    try:
        page_view.SetActiveDetail(System.Guid.Empty)
        sc.doc.Views.Redraw()
        return True
    except:
        pass
    return False


def get_page_viewport(page_view):
    if page_view is None:
        return None
    for attr_name in ["ActiveViewport", "MainViewport", "PageViewport"]:
        try:
            vp = getattr(page_view, attr_name)
            if vp:
                return vp
        except:
            pass
    return None


def find_layer_by_short_name(short_name):
    preferred = None
    fallback = None
    for layer in sc.doc.Layers:
        if layer is None:
            continue
        try:
            if layer.IsDeleted:
                continue
        except:
            pass
        try:
            if layer.Name == short_name:
                full_path = layer.FullPath
                if fallback is None:
                    fallback = full_path
                upper_path = full_path.upper()
                if "LAYOUT" in upper_path or "DRAWING" in upper_path:
                    preferred = full_path
                    break
        except:
            pass
    if preferred:
        return preferred
    if fallback:
        return fallback
    return None


def ensure_leader_layer():
    layer_name = find_layer_by_short_name(LEADER_LAYER_SHORT_NAME)
    if layer_name is None:
        try:
            layer_name = rs.AddLayer(LEADER_LAYER_SHORT_NAME, Color.Black, True, False)
        except:
            layer_name = rs.AddLayer(LEADER_LAYER_SHORT_NAME)
    try:
        rs.LayerVisible(layer_name, True)
    except:
        pass
    try:
        rs.LayerLocked(layer_name, False)
    except:
        pass
    return layer_name


def get_leaf_layer_name(layer_path):
    if not layer_path:
        return "UNLAYERED"
    if LABEL_USES_FULL_LAYER_PATH:
        return layer_path
    if "::" in layer_path:
        return layer_path.split("::")[-1]
    return layer_path


def get_object_layer_label(rh_obj):
    if rh_obj is None:
        return "UNLAYERED"
    try:
        layer_index = rh_obj.Attributes.LayerIndex
        layer = sc.doc.Layers[layer_index]
        return get_leaf_layer_name(layer.FullPath)
    except:
        pass
    try:
        return get_leaf_layer_name(rs.ObjectLayer(rh_obj.Id))
    except:
        pass
    return "UNLAYERED"


def try_set_enum_property(target, property_name, preferred_value_names):
    if target is None:
        return False
    try:
        prop = target.GetType().GetProperty(property_name)
    except:
        prop = None
    if prop is None:
        return False
    try:
        if not prop.CanWrite:
            return False
    except:
        pass
    try:
        enum_type = prop.PropertyType
        if not enum_type.IsEnum:
            return False
        for value_name in preferred_value_names:
            try:
                if System.Enum.IsDefined(enum_type, value_name):
                    enum_value = System.Enum.Parse(enum_type, value_name)
                    prop.SetValue(target, enum_value, None)
                    return True
            except:
                pass
    except:
        pass
    return False


def try_set_bool_property(target, property_name, value):
    if target is None:
        return False
    try:
        prop = target.GetType().GetProperty(property_name)
    except:
        prop = None
    if prop is None:
        return False
    try:
        if not prop.CanWrite:
            return False
    except:
        pass
    try:
        prop.SetValue(target, bool(value), None)
        return True
    except:
        pass
    try:
        setattr(target, property_name, bool(value))
        return True
    except:
        pass
    return False


def set_leader_object_text_orientation_horizontal(leader_id):
    """
    Best-effort object-level setting only.
    This uses the current document annotation style and does not create a new style.
    It also does not change the leader plane, which avoids moving the leader to the origin.
    """
    if leader_id is None:
        return False

    rh_obj = sc.doc.Objects.FindId(leader_id)
    if rh_obj is None:
        return False

    try:
        leader_geom = rh_obj.Geometry.Duplicate()
    except:
        return False

    changed = False
    horizontal_names = ["Horizontal", "Horiz", "InView"]

    enum_properties = [
        "TextOrientation",
        "LeaderTextOrientation",
        "LeaderContentAngleStyle",
        "LeaderTextAngleStyle",
        "TextAngleStyle",
        "DimensionTextOrientation",
        "AnnotationTextOrientation",
        "ContentAngleStyle",
        "TextAlignment",
        "LeaderTextAlignment"
    ]

    for prop_name in enum_properties:
        if try_set_enum_property(leader_geom, prop_name, horizontal_names):
            changed = True

    bool_true_properties = [
        "LeaderTextHorizontal",
        "TextHorizontal",
        "LeaderTextIsHorizontal",
        "ForceHorizontalText",
        "DrawTextHorizontal",
        "ContentAlwaysHorizontal"
    ]

    for prop_name in bool_true_properties:
        if try_set_bool_property(leader_geom, prop_name, True):
            changed = True

    if not changed:
        return False

    try:
        result = sc.doc.Objects.Replace(leader_id, leader_geom)
        sc.doc.Views.Redraw()
        return result
    except:
        return False


def get_detail_objects():
    detail_objects = []
    try:
        settings = Rhino.DocObjects.ObjectEnumeratorSettings()
        settings.NormalObjects = True
        settings.LockedObjects = True
        settings.HiddenObjects = False
        settings.DeletedObjects = False
        settings.ObjectTypeFilter = Rhino.DocObjects.ObjectType.Detail
        objs = sc.doc.Objects.GetObjectList(settings)
        for obj in objs:
            detail_objects.append(obj)
    except:
        try:
            settings = Rhino.DocObjects.ObjectEnumeratorSettings()
            settings.NormalObjects = True
            objs = sc.doc.Objects.GetObjectList(settings)
            for obj in objs:
                try:
                    if obj.ObjectType == Rhino.DocObjects.ObjectType.Detail:
                        detail_objects.append(obj)
                except:
                    pass
        except:
            pass
    return detail_objects


def point_in_bbox_xy(point, bbox, tol):
    if bbox is None:
        return False
    if not bbox.IsValid:
        return False
    if point.X < bbox.Min.X - tol:
        return False
    if point.X > bbox.Max.X + tol:
        return False
    if point.Y < bbox.Min.Y - tol:
        return False
    if point.Y > bbox.Max.Y + tol:
        return False
    return True


def find_detail_at_page_point(page_point):
    details = get_detail_objects()
    candidates = []
    for detail in details:
        try:
            bbox = detail.Geometry.GetBoundingBox(True)
        except:
            try:
                bbox = detail.GetBoundingBox(True)
            except:
                bbox = None
        if bbox and point_in_bbox_xy(page_point, bbox, 0.001):
            area = abs((bbox.Max.X - bbox.Min.X) * (bbox.Max.Y - bbox.Min.Y))
            candidates.append((area, detail))
    if not candidates:
        return None
    candidates.sort(key=lambda x: x[0])
    return candidates[0][1]


def transform_point(point, xform):
    p = Rhino.Geometry.Point3d(point.X, point.Y, point.Z)
    p.Transform(xform)
    return p


def distance_2d(a, b):
    dx = a.X - b.X
    dy = a.Y - b.Y
    return math.sqrt(dx * dx + dy * dy)


def point_segment_distance_2d(point, a, b):
    ax = a.X
    ay = a.Y
    bx = b.X
    by = b.Y
    px = point.X
    py = point.Y
    vx = bx - ax
    vy = by - ay
    wx = px - ax
    wy = py - ay
    denom = vx * vx + vy * vy
    if denom <= 0.000000001:
        return distance_2d(point, a)
    t = (wx * vx + wy * vy) / denom
    if t < 0.0:
        t = 0.0
    elif t > 1.0:
        t = 1.0
    cx = ax + t * vx
    cy = ay + t * vy
    dx = px - cx
    dy = py - cy
    return math.sqrt(dx * dx + dy * dy)


def polyline_distance_2d(screen_point, screen_points):
    if not screen_points:
        return 1000000000.0
    if len(screen_points) == 1:
        return distance_2d(screen_point, screen_points[0])
    best = 1000000000.0
    for i in range(len(screen_points) - 1):
        d = point_segment_distance_2d(screen_point, screen_points[i], screen_points[i + 1])
        if d < best:
            best = d
    return best


def sample_curve_to_screen_points(curve, world_to_screen, sample_count):
    screen_points = []
    if curve is None:
        return screen_points
    try:
        domain = curve.Domain
        start_t = domain.T0
        end_t = domain.T1
        for i in range(sample_count + 1):
            t = start_t + (end_t - start_t) * (float(i) / float(sample_count))
            p = curve.PointAt(t)
            sp = transform_point(p, world_to_screen)
            screen_points.append(sp)
    except:
        try:
            p0 = curve.PointAtStart
            p1 = curve.PointAtEnd
            screen_points.append(transform_point(p0, world_to_screen))
            screen_points.append(transform_point(p1, world_to_screen))
        except:
            pass
    return screen_points


def bbox_screen_distance(screen_point, bbox, world_to_screen):
    if bbox is None or not bbox.IsValid:
        return 1000000000.0
    try:
        corners = bbox.GetCorners()
    except:
        return 1000000000.0
    pts = []
    for c in corners:
        pts.append(transform_point(c, world_to_screen))
    edges = [(0, 1), (1, 2), (2, 3), (3, 0), (4, 5), (5, 6), (6, 7), (7, 4), (0, 4), (1, 5), (2, 6), (3, 7)]
    best = 1000000000.0
    for e in edges:
        d = point_segment_distance_2d(screen_point, pts[e[0]], pts[e[1]])
        if d < best:
            best = d
    return best


def object_projected_distance_pixels(rh_obj, screen_point, world_to_screen):
    if rh_obj is None:
        return 1000000000.0
    try:
        geom = rh_obj.Geometry
    except:
        return 1000000000.0
    if geom is None:
        return 1000000000.0

    obj_type = rh_obj.ObjectType
    best = 1000000000.0

    try:
        if obj_type == Rhino.DocObjects.ObjectType.Point:
            sp = transform_point(geom.Location, world_to_screen)
            return distance_2d(screen_point, sp)
    except:
        pass

    try:
        curve = geom if isinstance(geom, Rhino.Geometry.Curve) else None
        if curve:
            pts = sample_curve_to_screen_points(curve, world_to_screen, CURVE_SAMPLE_COUNT)
            return polyline_distance_2d(screen_point, pts)
    except:
        pass

    try:
        brep = None
        if isinstance(geom, Rhino.Geometry.Brep):
            brep = geom
        elif isinstance(geom, Rhino.Geometry.Surface):
            brep = geom.ToBrep()
        elif isinstance(geom, Rhino.Geometry.Extrusion):
            brep = geom.ToBrep()
        if brep:
            for edge in brep.Edges:
                pts = sample_curve_to_screen_points(edge, world_to_screen, BREP_EDGE_SAMPLE_COUNT)
                d = polyline_distance_2d(screen_point, pts)
                if d < best:
                    best = d
                if best < 1.0:
                    return best
            try:
                bbox = geom.GetBoundingBox(True)
                best = min(best, bbox_screen_distance(screen_point, bbox, world_to_screen))
            except:
                pass
            return best
    except:
        pass

    try:
        if isinstance(geom, Rhino.Geometry.Mesh):
            vertex_count = geom.Vertices.Count
            if vertex_count > 0:
                step = 1
                if vertex_count > 300:
                    step = int(vertex_count / 300)
                i = 0
                while i < vertex_count:
                    v = geom.Vertices[i]
                    p = Rhino.Geometry.Point3d(v.X, v.Y, v.Z)
                    sp = transform_point(p, world_to_screen)
                    d = distance_2d(screen_point, sp)
                    if d < best:
                        best = d
                    i += step
            try:
                bbox = geom.GetBoundingBox(True)
                best = min(best, bbox_screen_distance(screen_point, bbox, world_to_screen))
            except:
                pass
            return best
    except:
        pass

    try:
        bbox = geom.GetBoundingBox(True)
        best = min(best, bbox_screen_distance(screen_point, bbox, world_to_screen))
    except:
        pass
    return best


def get_model_objects_for_detail_search():
    model_objects = []
    try:
        settings = Rhino.DocObjects.ObjectEnumeratorSettings()
        settings.NormalObjects = True
        settings.LockedObjects = True
        settings.HiddenObjects = False
        settings.DeletedObjects = False
        objs = sc.doc.Objects.GetObjectList(settings)
    except:
        return model_objects

    for obj in objs:
        if obj is None:
            continue
        try:
            if obj.ObjectType == Rhino.DocObjects.ObjectType.Detail:
                continue
        except:
            pass
        try:
            if obj.Attributes.Space != Rhino.DocObjects.ActiveSpace.ModelSpace:
                continue
        except:
            pass
        try:
            if obj.ObjectType == Rhino.DocObjects.ObjectType.Annotation:
                continue
        except:
            pass
        try:
            layer = sc.doc.Layers[obj.Attributes.LayerIndex]
            if layer and not layer.IsVisible:
                continue
        except:
            pass
        model_objects.append(obj)
    return model_objects


def find_model_object_under_page_point(page_view, detail_obj, page_point):
    if page_view is None or detail_obj is None:
        return None, None
    page_vp = get_page_viewport(page_view)
    if page_vp is None:
        return None, None
    try:
        detail_vp = detail_obj.Viewport
    except:
        detail_vp = None
    if detail_vp is None:
        return None, None
    try:
        cs = Rhino.DocObjects.CoordinateSystem
        page_to_screen = page_vp.GetTransform(cs.World, cs.Screen)
        model_to_screen = detail_vp.GetTransform(cs.World, cs.Screen)
    except:
        return None, None

    screen_point = transform_point(page_point, page_to_screen)
    best_obj = None
    best_distance = 1000000000.0
    model_objects = get_model_objects_for_detail_search()
    for obj in model_objects:
        d = object_projected_distance_pixels(obj, screen_point, model_to_screen)
        if d < best_distance:
            best_distance = d
            best_obj = obj

    if best_obj is None:
        return None, None
    if best_distance > PICK_TOLERANCE_PIXELS:
        return None, best_distance
    return best_obj, best_distance


def get_layout_tip_point():
    gp = Rhino.Input.Custom.GetPoint()
    gp.SetCommandPrompt("Click the leader tip on the layout detail, snapped to the object to label")
    gp.AcceptNothing(False)
    result = gp.Get()
    if result == Rhino.Input.GetResult.Point:
        return gp.Point()
    return None


def collect_remaining_leader_points(tip_point):
    points = [tip_point]
    previous_point = tip_point
    while len(points) < MAX_LEADER_POINTS:
        gp = Rhino.Input.Custom.GetPoint()
        if len(points) == 1:
            gp.SetCommandPrompt("Place the leader elbow point")
            gp.AcceptNothing(False)
        else:
            gp.SetCommandPrompt("Place another leader point / text location, or press Enter to finish")
            gp.AcceptNothing(True)
        try:
            gp.SetBasePoint(previous_point, True)
            gp.DrawLineFromPoint(previous_point, True)
        except:
            pass
        result = gp.Get()
        if result == Rhino.Input.GetResult.Point:
            p = gp.Point()
            points.append(p)
            previous_point = p
        elif result == Rhino.Input.GetResult.Nothing:
            if len(points) >= 2:
                break
        else:
            return None
    if len(points) < 2:
        return None
    return points


def remove_consecutive_duplicate_points(points, tolerance):
    if not points:
        return []
    cleaned = [points[0]]
    for i in range(1, len(points)):
        p = points[i]
        last = cleaned[-1]
        if distance_2d(p, last) > tolerance:
            cleaned.append(p)
    return cleaned


def rotate_point_around_anchor(point, anchor, angle_degrees):
    angle_radians = math.radians(angle_degrees)
    c = math.cos(angle_radians)
    s = math.sin(angle_radians)
    dx = point.X - anchor.X
    dy = point.Y - anchor.Y
    x = anchor.X + dx * c - dy * s
    y = anchor.Y + dx * s + dy * c
    return Rhino.Geometry.Point3d(x, y, point.Z)


def rotate_points_around_anchor(points, anchor, angle_degrees):
    rotated = []
    for p in points:
        rotated.append(rotate_point_around_anchor(p, anchor, angle_degrees))
    return rotated


def rotate_object_around_anchor(object_id, anchor, angle_degrees):
    if object_id is None:
        return False
    try:
        rs.RotateObject(object_id, anchor, angle_degrees, [0, 0, 1], False)
        return True
    except:
        pass
    try:
        xform = Rhino.Geometry.Transform.Rotation(math.radians(angle_degrees), Rhino.Geometry.Vector3d.ZAxis, anchor)
        sc.doc.Objects.Transform(object_id, xform, True)
        sc.doc.Views.Redraw()
        return True
    except:
        pass
    return False


def force_object_to_page_space(object_id, page_view):
    if object_id is None or page_view is None:
        return
    rh_obj = sc.doc.Objects.FindId(object_id)
    if rh_obj is None:
        return
    viewport_id = System.Guid.Empty
    try:
        page_vp = get_page_viewport(page_view)
        if page_vp:
            viewport_id = page_vp.Id
    except:
        pass
    try:
        attr = rh_obj.Attributes.Duplicate()
        attr.Space = Rhino.DocObjects.ActiveSpace.PageSpace
        if viewport_id != System.Guid.Empty:
            attr.ViewportId = viewport_id
        sc.doc.Objects.ModifyAttributes(rh_obj, attr, True)
    except:
        pass


def add_layout_leader(points, label_text, page_view):
    if not points or len(points) < 2:
        return None

    leader_layer = ensure_leader_layer()
    old_layer = None

    try:
        old_layer = rs.CurrentLayer()
        rs.CurrentLayer(leader_layer)
    except:
        pass

    set_page_as_active(page_view)

    # Use the final picked point as the rotation anchor. This is typically the leader text location.
    # Pre-rotate points -90, create the leader, then rotate the object +90.
    # This preserves the final visible leader point locations while applying the rotation correction.
    clean_points = remove_consecutive_duplicate_points(points, 0.000001)
    anchor_point = clean_points[-1]
    pre_rotated_points = rotate_points_around_anchor(clean_points, anchor_point, -LEADER_CORRECTION_ROTATION_DEGREES)

    leader_id = None
    try:
        # Use the current annotation style and the current page context.
        leader_id = rs.AddLeader(pre_rotated_points, None, label_text)
    except:
        leader_id = None

    if leader_id:
        force_object_to_page_space(leader_id, page_view)

        # Set horizontal behavior before the corrective object rotation.
        # Do not create/switch annotation styles and do not change the leader plane.
        set_leader_object_text_orientation_horizontal(leader_id)

        rotate_object_around_anchor(leader_id, anchor_point, LEADER_CORRECTION_ROTATION_DEGREES)
        force_object_to_page_space(leader_id, page_view)

        try:
            rs.ObjectLayer(leader_id, leader_layer)
        except:
            pass
        try:
            rs.ObjectName(leader_id, "Layer Leader - " + label_text)
        except:
            pass

    if old_layer:
        try:
            rs.CurrentLayer(old_layer)
        except:
            pass

    return leader_id


def main():
    page_view = get_active_page_view()
    if page_view is None:
        print "This tool must be run from a layout page view. Please switch to a layout and run it again."
        return

    set_page_as_active(page_view)

    tip_point = get_layout_tip_point()
    if tip_point is None:
        print "Leader creation cancelled before placing the tip point."
        return

    detail_obj = find_detail_at_page_point(tip_point)
    if detail_obj is None:
        print "No detail view was found under the clicked point. Click directly on top of a layout detail view."
        return

    model_obj, projected_distance = find_model_object_under_page_point(page_view, detail_obj, tip_point)
    if model_obj is None:
        if projected_distance is None:
            print "Could not evaluate model geometry through the clicked detail."
        else:
            print "No model object was close enough to the clicked point through the detail. Closest projected distance: " + str(round(projected_distance, 2)) + " pixels."
        return

    label_text = get_object_layer_label(model_obj)

    try:
        rs.UnselectAllObjects()
    except:
        pass

    leader_points = collect_remaining_leader_points(tip_point)
    if not leader_points:
        print "Leader creation cancelled before enough leader points were placed."
        return

    rs.EnableRedraw(False)
    leader_id = add_layout_leader(leader_points, label_text, page_view)
    rs.EnableRedraw(True)

    if leader_id:
        try:
            rs.SelectObject(leader_id)
        except:
            pass
        print "Created layout leader label using current annotation style with 90-degree correction: " + label_text
    else:
        print "The leader could not be created on the layout page."


main()
