import rhinoscriptsyntax as rs

pm = rs.GetDocumentData("Lab_Data", "project_manager")
date = rs.GetDocumentData("Lab_Data", "date_modified")
project = rs.GetDocumentData("Lab_Data", "project")
designer = rs.GetDocumentData("Lab_Data", "designer")

num_layouts = rs.LayerChildCount("LAYOUTS")
layouts = rs.LayerChildren("LAYOUTS")


for layout in layouts:
    project_layer = layout + "::text" + "::" + "Project Name"
    manager_layer = layout + "::text" + "::" + "Project Manager"
    date_layer = layout + "::text" + "::" + "Date"
    file_name_layer = layout + "::text" + "::" + "File Name"
    designer_layer = layout + "::text::" + "Designer"
    
    
    project_obj = rs.ObjectsByLayer(project_layer)
    manager_obj = rs.ObjectsByLayer(manager_layer)
    date_obj = rs.ObjectsByLayer(date_layer)
    file_name_obj = rs.ObjectsByLayer(file_name_layer)
    designer_name_obj = rs.ObjectsByLayer(designer_layer)
    
    rs.TextObjectText(project_obj, project)
    rs.TextObjectText(manager_obj, pm)
    rs.TextObjectText(date_obj, date)
    rs.TextObjectText(file_name_obj, rs.DocumentName())
    rs.TextObjectText(designer_name_obj, designer)