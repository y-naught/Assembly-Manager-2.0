import rhinoscriptsyntax as rs
import scriptcontext as sc
import Rhino
import Rhino.Geometry as rg
from System.Drawing import Color


PREVIEW_COLOR = Color.FromArgb(80, 120, 200, 255)
PREVIEW_LINE_COLOR = Color.FromArgb(255, 40, 120, 255)


def point_to_point3d(point):
    """Convert a RhinoScript point/list-like point to Rhino.Geometry.Point3d."""
    return rg.Point3d(point[0], point[1], point[2])


def get_current_or_requested_objects():
    """Use currently selected objects if present; otherwise ask the user to select objects."""
    selected_objects = rs.SelectedObjects(False, False)
    if selected_objects and len(selected_objects) > 0:
        return selected_objects

    objects = rs.GetObjects(
        "Select object(s) to move orthogonally",
        preselect=False,
        select=True
    )
    if objects and len(objects) > 0:
        return objects

    return None


def get_axis_from_user():
    """Prompt for a World axis letter after object selection. Default is Z."""
    while True:
        axis_text = rs.GetString("Move along which World axis? Type X, Y, or Z", "Z")
        if axis_text is None:
            return None

        axis_text = axis_text.strip().upper()
        if axis_text in ["X", "Y", "Z"]:
            return axis_text

        print("Please type only X, Y, or Z, then press Enter.")


def constrained_vector(axis_letter, base_point, target_point):
    """Project the base-to-target displacement onto one selected World axis."""
    dx = target_point.X - base_point.X
    dy = target_point.Y - base_point.Y
    dz = target_point.Z - base_point.Z

    if axis_letter == "X":
        return rg.Vector3d(dx, 0.0, 0.0)
    if axis_letter == "Y":
        return rg.Vector3d(0.0, dy, 0.0)
    if axis_letter == "Z":
        return rg.Vector3d(0.0, 0.0, dz)

    return rg.Vector3d(0.0, 0.0, 0.0)


def constrained_target_point(axis_letter, base_point, target_point):
    """Return the visible constrained end point used for preview linework."""
    vector = constrained_vector(axis_letter, base_point, target_point)
    return rg.Point3d(base_point.X + vector.X, base_point.Y + vector.Y, base_point.Z + vector.Z)


def vector_length(vector):
    return (vector.X * vector.X + vector.Y * vector.Y + vector.Z * vector.Z) ** 0.5


def draw_fallback_preview(display, rhino_object, xform):
    """Fallback preview drawing if Display.DrawObject with transform is unavailable."""
    try:
        geometry = rhino_object.Geometry.Duplicate()
        if geometry is None:
            return
        geometry.Transform(xform)

        if isinstance(geometry, rg.Curve):
            display.DrawCurve(geometry, PREVIEW_LINE_COLOR, 2)
        elif isinstance(geometry, rg.Brep):
            try:
                display.DrawBrepWires(geometry, PREVIEW_LINE_COLOR, 1)
            except:
                pass
        elif isinstance(geometry, rg.Extrusion):
            brep = geometry.ToBrep()
            if brep:
                display.DrawBrepWires(brep, PREVIEW_LINE_COLOR, 1)
        elif isinstance(geometry, rg.Mesh):
            try:
                display.DrawMeshWires(geometry, PREVIEW_LINE_COLOR)
            except:
                pass
        elif isinstance(geometry, rg.Point):
            try:
                display.DrawPoint(geometry.Location, PREVIEW_LINE_COLOR)
            except:
                pass
    except:
        pass


class OrthogonalMovePointGetter(Rhino.Input.Custom.GetPoint):
    def __init__(self, object_ids, axis_letter, base_point):
        Rhino.Input.Custom.GetPoint.__init__(self)
        self.object_ids = object_ids
        self.axis_letter = axis_letter
        self.base_point = base_point
        self.rhino_objects = []

        for object_id in object_ids:
            rhino_object = sc.doc.Objects.Find(object_id)
            if rhino_object:
                self.rhino_objects.append(rhino_object)

    def OnDynamicDraw(self, e):
        Rhino.Input.Custom.GetPoint.OnDynamicDraw(self, e)

        current_point = e.CurrentPoint
        move_vector = constrained_vector(self.axis_letter, self.base_point, current_point)
        preview_target = constrained_target_point(self.axis_letter, self.base_point, current_point)
        xform = rg.Transform.Translation(move_vector)

        # Draw a constrained axis-only guide line from the picked base point.
        try:
            e.Display.DrawLine(self.base_point, preview_target, PREVIEW_LINE_COLOR, 2)
        except:
            pass

        # Draw a dynamic transformed preview of the selected objects.
        for rhino_object in self.rhino_objects:
            try:
                e.Display.DrawObject(rhino_object, xform)
            except:
                draw_fallback_preview(e.Display, rhino_object, xform)


def get_end_point_with_dynamic_preview(object_ids, axis_letter, base_point):
    """Get the second point while showing an axis-projected movement preview."""
    getter = OrthogonalMovePointGetter(object_ids, axis_letter, base_point)
    getter.SetCommandPrompt("Pick second point; movement previews only along World {0}".format(axis_letter))
    getter.SetBasePoint(base_point, True)
    getter.DrawLineFromPoint(base_point, False)

    result = getter.Get()
    if result == Rhino.Input.GetResult.Point:
        return getter.Point()

    return None


def main():
    # 1. First use preselected objects if any; otherwise ask for object selection.
    objects = get_current_or_requested_objects()
    if not objects:
        print("Orthogonal Move cancelled: no objects selected.")
        return

    # 2. Then ask for the axis. Default is now World Z.
    axis_letter = get_axis_from_user()
    if axis_letter is None:
        print("Orthogonal Move cancelled: no axis selected.")
        return

    # 3. Then ask for the base point.
    base_point_raw = rs.GetPoint("Pick base point for World {0} move".format(axis_letter))
    if base_point_raw is None:
        print("Orthogonal Move cancelled: no base point selected.")
        return
    base_point = point_to_point3d(base_point_raw)

    # 4. Then ask for the second point with live constrained preview.
    target_point = get_end_point_with_dynamic_preview(objects, axis_letter, base_point)
    if target_point is None:
        print("Orthogonal Move cancelled: no second point selected.")
        return

    # 5. Apply only the selected World-axis component of the displacement.
    move_vector = constrained_vector(axis_letter, base_point, target_point)
    if vector_length(move_vector) <= 0.0000001:
        print("No movement applied: the picked points have no displacement along World {0}.".format(axis_letter))
        return

    rs.EnableRedraw(False)
    moved_objects = rs.MoveObjects(objects, (move_vector.X, move_vector.Y, move_vector.Z))
    rs.EnableRedraw(True)
    sc.doc.Views.Redraw()

    if moved_objects:
        rs.SelectObjects(moved_objects)
        print("Moved {0} object(s) along World {1}.".format(len(moved_objects), axis_letter))
    else:
        print("Move failed. Please check whether the selected objects or layers are locked.")


main()
