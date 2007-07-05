//
// GuiBuilderService.cs
//
// Author:
//   Lluis Sanchez Gual
//
// Copyright (C) 2006 Novell, Inc (http://www.novell.com)
//
// Permission is hereby granted, free of charge, to any person obtaining
// a copy of this software and associated documentation files (the
// "Software"), to deal in the Software without restriction, including
// without limitation the rights to use, copy, modify, merge, publish,
// distribute, sublicense, and/or sell copies of the Software, and to
// permit persons to whom the Software is furnished to do so, subject to
// the following conditions:
// 
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
// LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
// OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
// WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
//

using System;
using System.IO;
using System.Reflection;
using System.Collections;
using System.CodeDom;
using System.CodeDom.Compiler;

using MonoDevelop.Ide.Gui;
using MonoDevelop.Ide.Gui.Content;
using MonoDevelop.Projects;
using MonoDevelop.Projects.Parser;
using MonoDevelop.Projects.CodeGeneration;
using MonoDevelop.Core.Gui;
using MonoDevelop.Projects.Text;
using MonoDevelop.Core;
using MonoDevelop.Core.Execution;
using MonoDevelop.Deployment;
using Mono.Cecil;

namespace MonoDevelop.GtkCore.GuiBuilder
{
	class GuiBuilderService
	{
		static GuiBuilderProjectPad widgetTreePad;
		static string GuiBuilderLayout = "GUI Builder";
		static string defaultLayout;
	
		static Stetic.Application steticApp;
		
		static bool generating;
		static Stetic.CodeGenerationResult generationResult = null;
		static Exception generatedException = null;
		
		static Stetic.IsolationMode IsolationMode = Stetic.IsolationMode.None;
//		static Stetic.IsolationMode IsolationMode = Stetic.IsolationMode.ProcessUnix;
		
		static GuiBuilderService ()
		{
			if (IdeApp.Workbench == null)
				return;
			IdeApp.Workbench.ActiveDocumentChanged += new EventHandler (OnActiveDocumentChanged);
			IdeApp.ProjectOperations.StartBuild += OnBeforeCompile;
			IdeApp.ProjectOperations.EndBuild += OnProjectCompiled;
			IdeApp.ProjectOperations.ParserDatabase.AssemblyInformationChanged += (AssemblyInformationEventHandler) MonoDevelop.Core.Gui.Services.DispatchService.GuiDispatch (new AssemblyInformationEventHandler (OnAssemblyInfoChanged));
			
			IdeApp.Exited += delegate {
				if (steticApp != null) {
					StoreConfiguration ();
					steticApp.Dispose ();
				}
			};
		}
		
		internal static GuiBuilderProjectPad WidgetTreePad {
			get { return widgetTreePad; }
			set { widgetTreePad = value; }
		}
		
		public static Stetic.Application SteticApp {
			get {
				if (steticApp == null) {
					steticApp = new Stetic.Application (IsolationMode);
					steticApp.AllowInProcLibraries = false;
					steticApp.ShowNonContainerWarning = Runtime.Properties.GetProperty ("MonoDevelop.GtkCore.ShowNonContainerWarning", true);
				}
				return steticApp;
			}
		}
		
		internal static void StoreConfiguration ()
		{
			Runtime.Properties.SetProperty ("MonoDevelop.GtkCore.ShowNonContainerWarning", steticApp.ShowNonContainerWarning);
			Runtime.Properties.SaveProperties ();
		}

		
		public static GuiBuilderProject GetGuiBuilderProject (Project project)
		{
			GtkDesignInfo info = GtkCoreService.GetGtkInfo (project);
			if (info != null)
				return info.GuiBuilderProject;
			else
				return null;
		}
		
		public static ActionGroupView OpenActionGroup (Project project, Stetic.ActionGroupInfo group)
		{
			GuiBuilderProject p = GetGuiBuilderProject (project);
			string file = p != null ? p.GetSourceCodeFile (group) : null;
			if (file == null) {
				file = ActionGroupDisplayBinding.BindToClass (project, group);
			}
			
			Document doc = IdeApp.Workbench.OpenDocument (file, true);
			if (doc != null) {
				ActionGroupView view = doc.GetContent<ActionGroupView> ();
				if (view != null) {
					view.ShowDesignerView ();
					return view;
				}
			}
			return null;
		}
		
		static void OnActiveDocumentChanged (object s, EventArgs args)
		{
			if (IdeApp.Workbench.ActiveDocument == null) {
				if (SteticApp.ActiveProject != null) {
					SteticApp.ActiveProject = null;
					RestoreLayout ();
				}
				return;
			}

			GuiBuilderView view = IdeApp.Workbench.ActiveDocument.GetContent<GuiBuilderView> ();
			if (view != null) {
				view.SetActive ();
				SetDesignerLayout ();
			}
			else if (IdeApp.Workbench.ActiveDocument.GetContent<ActionGroupView> () != null) {
				if (SteticApp.ActiveProject != null) {
					SteticApp.ActiveProject = null;
					SetDesignerLayout ();
				}
			} else {
				if (SteticApp.ActiveProject != null) {
					SteticApp.ActiveProject = null;
					RestoreLayout ();
				}
			}
		}
		
		static void SetDesignerLayout ()
		{
			if (IdeApp.Workbench.CurrentLayout != GuiBuilderLayout) {
				// Added a delay here to avoid a conflict between the tab switch and the layout switch.
				GLib.Timeout.Add (100, delegate {
					bool exists = Array.IndexOf (IdeApp.Workbench.Layouts, GuiBuilderLayout) != -1;
					defaultLayout = IdeApp.Workbench.CurrentLayout;
					IdeApp.Workbench.CurrentLayout = GuiBuilderLayout;
					if (!exists) {
						Pad p = IdeApp.Workbench.GetPad<MonoDevelop.DesignerSupport.ToolboxPad> ();
						if (p != null) p.Visible = true;
						p = IdeApp.Workbench.GetPad<MonoDevelop.DesignerSupport.PropertyPad> ();
						if (p != null) p.Visible = true;
					}
					return false;
				});
			}
		}
		
		static void RestoreLayout ()
		{
			if (defaultLayout != null) {
				// Added a delay here to avoid a conflict between the tab switch and the layout switch.
				GLib.Timeout.Add (100, delegate {
					IdeApp.Workbench.CurrentLayout = defaultLayout;
					defaultLayout = null;
					return false;
				});
			}
		}
		
		static void OnBeforeCompile (object s, BuildEventArgs args)
		{
			if (IdeApp.ProjectOperations.CurrentOpenCombine == null)
				return;

			// Generate stetic files for all modified projects
			GtkProjectServiceExtension.GenerateSteticCode = true;
		}

		static void OnProjectCompiled (object s, BuildEventArgs args)
		{
			if (args.Success) {
				// Unload stetic projects which are not currently
				// being used by the IDE. This will avoid unnecessary updates.
				if (IdeApp.ProjectOperations.CurrentOpenCombine != null) {
					foreach (Project prj in IdeApp.ProjectOperations.CurrentOpenCombine.GetAllProjects ()) {
						GtkDesignInfo info = GtkCoreService.GetGtkInfo (prj);
						if (info != null && !HasOpenDesigners (prj, false)) {
							info.ReloadGuiBuilderProject ();
						}
					}
				}
				
				SteticApp.UpdateWidgetLibraries (false);
			}
			else {
				// Some gtk# packages don't include the .pc file unless you install gtk-sharp-devel
				if (Runtime.SystemAssemblyService.GetPackage ("gtk-sharp-2.0") == null) {
					string msg = GettextCatalog.GetString ("ERROR: MonoDevelop could not find the Gtk# 2.0 development package. Compilation of projects depending on Gtk# libraries will fail. You may need to install development packages for gtk-sharp-2.0.");
					args.ProgressMonitor.Log.WriteLine ();
					args.ProgressMonitor.Log.WriteLine (msg);
				}
			}
		}
		
		internal static bool HasOpenDesigners (Project project, bool modifiedOnly)
		{
			foreach (Document doc in IdeApp.Workbench.Documents) {
				if ((doc.GetContent<GuiBuilderView>() != null || doc.GetContent<ActionGroupView>() != null) && doc.Project == project && (!modifiedOnly || doc.IsDirty))
					return true;
			}
			return false;
		}
		
		static void OnAssemblyInfoChanged (object s, AssemblyInformationEventArgs args)
		{
			//SteticApp.UpdateWidgetLibraries (false);
		}

		internal static void AddCurrentWidgetToClass ()
		{
			if (IdeApp.Workbench.ActiveDocument != null) {
				GuiBuilderView view = IdeApp.Workbench.ActiveDocument.GetContent<GuiBuilderView> ();
				if (view != null)
					view.AddCurrentWidgetToClass ();
			}
		}
		
		internal static void JumpToSignalHandler (Stetic.Signal signal)
		{
			if (IdeApp.Workbench.ActiveDocument != null) {
				CombinedDesignView view = IdeApp.Workbench.ActiveDocument.GetContent<CombinedDesignView> ();
				if (view != null)
					view.JumpToSignalHandler (signal);
			}
		}
		
		public static void ImportGladeFile (Project project)
		{
			GtkDesignInfo info = GtkCoreService.GetGtkInfo (project);
			if (info == null) info = GtkCoreService.EnableGtkSupport (project);
			info.GuiBuilderProject.ImportGladeFile ();
		}
		
		public static string GetBuildCodeFileName (Project project, string componentName)
		{
			GtkDesignInfo info = GtkCoreService.GetGtkInfo (project);
			return Path.Combine (info.GtkGuiFolder, componentName + Path.GetExtension (info.SteticGeneratedFile));
		}
		
		public static string GenerateSteticCodeStructure (DotNetProject project, Stetic.ProjectItemInfo item, bool saveToFile, bool overwrite)
		{
			return GenerateSteticCodeStructure (project, item, null, saveToFile, overwrite);
		}
		
		public static string GenerateSteticCodeStructure (DotNetProject project, Stetic.Component component, bool saveToFile, bool overwrite)
		{
			return GenerateSteticCodeStructure (project, null, component, saveToFile, overwrite);
		}
		
		static string GenerateSteticCodeStructure (DotNetProject project, Stetic.ProjectItemInfo item, Stetic.Component component, bool saveToFile, bool overwrite)
		{
			// Generate a class which contains fields for all bound widgets of the component
			
			string name = item != null ? item.Name : component.Name;
			string fileName = GetBuildCodeFileName (project, name);
			
			string ns = "";
			int i = name.LastIndexOf ('.');
			if (i != -1) {
				ns = name.Substring (0, i);
				name = name.Substring (i+1);
			}
			
			GtkDesignInfo info = GtkCoreService.GetGtkInfo (project);
			
			if (saveToFile && !overwrite && File.Exists (fileName))
				return fileName;
			
			if (item != null)
				component = item.Component;
			
			CodeCompileUnit cu = new CodeCompileUnit ();
			
			if (info.GeneratePartialClasses) {
				CodeNamespace cns = new CodeNamespace (ns);
				cu.Namespaces.Add (cns);
				
				CodeTypeDeclaration type = new CodeTypeDeclaration (name);
				type.IsPartial = true;
				type.Attributes = MemberAttributes.Public;
				type.TypeAttributes = System.Reflection.TypeAttributes.Public;
				cns.Types.Add (type);
				
				foreach (Stetic.ObjectBindInfo binfo in component.GetObjectBindInfo ()) {
					type.Members.Add (
						new CodeMemberField (
							binfo.TypeName,
							binfo.Name
						)
					);
				}
			}
			else {
				if (!saveToFile)
					return fileName;
				CodeNamespace cns = new CodeNamespace ();
				cns.Comments.Add (new CodeCommentStatement ("Generated code for component " + component.Name));
				cu.Namespaces.Add (cns);
			}
			
			CodeDomProvider provider = project.LanguageBinding.GetCodeDomProvider ();
			if (provider == null)
				throw new UserException ("Code generation not supported for language: " + project.LanguageName);
			
			ICodeGenerator gen = provider.CreateGenerator ();
			TextWriter fileStream;
			if (saveToFile)
				fileStream = new StreamWriter (fileName);
			else
				fileStream = new StringWriter ();
			
			try {
				gen.GenerateCodeFromCompileUnit (cu, fileStream, new CodeGeneratorOptions ());
			} finally {
				fileStream.Close ();
			}

			if (IdeApp.ProjectOperations.ParserDatabase.IsLoaded (project)) {
				// Only update the parser database if the project is actually loaded in the IDE.
				if (saveToFile)
					IdeApp.ProjectOperations.ParserDatabase.GetProjectParserContext (project).UpdateDatabase ();
				else
					IdeApp.ProjectOperations.ParserDatabase.UpdateFile (project, fileName, ((StringWriter)fileStream).ToString ());
			}

			return fileName;
		}
		
		
		public static Stetic.CodeGenerationResult GenerateSteticCode (IProgressMonitor monitor, Project prj)
		{
			if (generating)
				return null;

			DotNetProject project = prj as DotNetProject;
			if (project == null)
				return null;
				
			GtkDesignInfo info = GtkCoreService.GetGtkInfo (project);
			if (info == null)
				return null;
			
			// Check if the stetic file has been modified since last generation
			if (File.Exists (info.SteticGeneratedFile) && File.Exists (info.SteticFile)) {
				if (File.GetLastWriteTime (info.SteticGeneratedFile) > File.GetLastWriteTime (info.SteticFile))
					return null;
			}
			
			if (info.GuiBuilderProject.HasError) {
				monitor.ReportError (GettextCatalog.GetString ("GUI code generation failed for project '{0}'. The file '{1}' could not be loaded.", project.Name, info.SteticFile), null);
				monitor.AsyncOperation.Cancel ();
				return null;
			}
			
			if (info.GuiBuilderProject.IsEmpty) 
				return null;

			monitor.Log.WriteLine (GettextCatalog.GetString ("Generating GUI code for project '{0}'...", project.Name));
			
			// Make sure the referenced assemblies are up to date. It is necessary to do
			// it now since they may contain widget libraries.
			prj.CopyReferencesToOutputPath (false);
			
			info.GuiBuilderProject.UpdateLibraries ();
			
			if (info.IsWidgetLibrary) {
				// Make sure the widget export file is up to date.
				GtkCoreService.UpdateObjectsFile (project);
			}

			ArrayList projects = new ArrayList ();
			projects.Add (info.GuiBuilderProject.File);
			
			generating = true;
			generationResult = null;
			generatedException = null;
			
			bool canGenerateInProcess = IsolationMode != Stetic.IsolationMode.None || info.GuiBuilderProject.SteticProject.CanGenerateCode;
			
			// Run the generation in another thread to avoid freezing the GUI
			System.Threading.ThreadPool.QueueUserWorkItem ( delegate {
				try {
					if (!canGenerateInProcess) {
						// Generate the code in another process if stetic is not isolated
						CodeGeneratorProcess cob = (CodeGeneratorProcess) Runtime.ProcessService.CreateExternalProcessObject (typeof (CodeGeneratorProcess), false);
						using (cob) {
							generationResult = cob.GenerateCode (projects, info.GenerateGettext, info.GettextClass, info.GeneratePartialClasses);
						}
					} else {
						// No need to create another process, since stetic has its own backend process
						// or the widget libraries have no custom wrappers
						Stetic.GenerationOptions options = new Stetic.GenerationOptions ();
						options.UseGettext = info.GenerateGettext;
						options.GettextClass = info.GettextClass;
						options.UsePartialClasses = info.GeneratePartialClasses;
						options.GenerateSingleFile = false;
						generationResult = SteticApp.GenerateProjectCode (options, info.GuiBuilderProject.SteticProject);
					}
				} catch (Exception ex) {
					generatedException = ex;
				} finally {
					generating = false;
				}
			});
			
			while (generating) {
				IdeApp.Services.DispatchService.RunPendingEvents ();
				System.Threading.Thread.Sleep (100);
			}
			
			if (generatedException != null)
				throw new UserException ("GUI code generation failed: " + generatedException.Message);
			
			if (generationResult == null)
				return null;
				
			CodeDomProvider provider = project.LanguageBinding.GetCodeDomProvider ();
			if (provider == null)
				throw new UserException ("Code generation not supported in language: " + project.LanguageName);
			
			ICodeGenerator gen = provider.CreateGenerator ();
			string basePath = Path.GetDirectoryName (info.SteticGeneratedFile);
			string ext = Path.GetExtension (info.SteticGeneratedFile);
			
			foreach (Stetic.SteticCompilationUnit unit in generationResult.Units) {
				string fname;
				if (unit.Name.Length == 0)
					fname = info.SteticGeneratedFile;
				else
					fname = Path.Combine (basePath, unit.Name) + ext;
				StreamWriter fileStream = new StreamWriter (fname);
				try {
					gen.GenerateCodeFromCompileUnit (unit, fileStream, new CodeGeneratorOptions ());
				} finally {
					fileStream.Close ();
				}
			}
			
			// Make sure the generated files are added to the project
			if (info.UpdateGtkFolder ()) {
				Gtk.Application.Invoke (delegate {
					IdeApp.ProjectOperations.SaveProject (project);
				});
			}
			
			return generationResult;
		}
		
		internal static string ImportFile (Project prj, string file)
		{
			ProjectFile pfile = prj.ProjectFiles.GetFile (file);
			if (pfile == null) {
				string[] files = IdeApp.ProjectOperations.AddFilesToProject (prj, new string[] { file }, prj.BaseDirectory);
				if (files.Length == 0)
					return null;
				if (files [0] == null)
					return null;
				pfile = prj.ProjectFiles.GetFile (files[0]);
			}
			if (pfile.BuildAction == BuildAction.EmbedAsResource) {
				if (!IdeApp.Services.MessageService.AskQuestion (GettextCatalog.GetString ("You are requesting the file '{0}' to be used as source for an image. However, this file is already added to the project as a resource. Are you sure you want to continue (the file will have to be removed from the resource list)?")))
					return null;
			}
			pfile.BuildAction = BuildAction.FileCopy;
			DeployProperties props = DeployService.GetDeployProperties (pfile);
			props.UseProjectRelativePath = true;
			return pfile.FilePath;
		}
		
	}


	public class CodeGeneratorProcess: RemoteProcessObject
	{
		public Stetic.CodeGenerationResult GenerateCode (ArrayList projectFiles, bool useGettext, string gettextClass, bool usePartialClasses)
		{
			Gtk.Application.Init ();
			
			Stetic.Application app = new Stetic.Application (Stetic.IsolationMode.None);
			
			Stetic.Project[] projects = new Stetic.Project [projectFiles.Count];
			for (int n=0; n < projectFiles.Count; n++) {
				projects [n] = app.CreateProject ();
				projects [n].Load ((string) projectFiles [n]);
			}
			
			Stetic.GenerationOptions options = new Stetic.GenerationOptions ();
			options.UseGettext = useGettext;
			options.GettextClass = gettextClass;
			options.UsePartialClasses = usePartialClasses;
			options.GenerateSingleFile = false;
			
			return app.GenerateProjectCode (options, projects);
		}
	}
}
