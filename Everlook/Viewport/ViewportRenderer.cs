﻿//
//  ViewportRenderer.cs
//
//  Author:
//       Jarl Gullberg <jarl.gullberg@gmail.com>
//
//  Copyright (c) 2017 Jarl Gullberg
//
//  This program is free software: you can redistribute it and/or modify
//  it under the terms of the GNU General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
//
//  This program is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
//  GNU General Public License for more details.
//
//  You should have received a copy of the GNU General Public License
//  along with this program.  If not, see <http://www.gnu.org/licenses/>.
//

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Everlook.Configuration;
using Everlook.UI.Widgets;
using Everlook.Viewport.Camera;
using Everlook.Viewport.Rendering.Core;
using Everlook.Viewport.Rendering.Interfaces;
using Gdk;
using log4net;
using OpenTK;
using OpenTK.Graphics.OpenGL;
using OpenTK.Input;

namespace Everlook.Viewport
{
	/// <summary>
	/// Viewport renderer for the main Everlook UI. This class manages an OpenGL rendering thread, which
	/// uses rendering built into the different renderable objects
	/// </summary>
	public sealed class ViewportRenderer : IDisposable
	{
		/// <summary>
		/// Logger instance for this class.
		/// </summary>
		private static readonly ILog Log = LogManager.GetLogger(typeof(ViewportRenderer));

		/// <summary>
		/// The viewport widget displayed to the user in the main interface.
		/// Used to get proper dimensions for the OpenGL viewport.
		/// </summary>
		private readonly ViewportArea ViewportWidget;

		/*
			RenderTarget and related control flow data.
		*/

		/// <summary>
		/// A lock object used to enforce that the rendering target can finish its current
		/// frame before a new one is assigned.
		/// </summary>
		private readonly object RenderTargetLock = new object();

		/// <summary>
		/// Gets or sets a value indicating whether this <see cref="ViewportRenderer"/> has been disposed.
		/// </summary>
		private bool IsDisposed { get; set; }

		/// <summary>
		/// Gets a value indicating whether or not the renderer currently has an object to render.
		/// </summary>
		public bool HasRenderTarget => this.RenderTarget != null;

		/// <summary>
		/// Gets the current rendering target. This is an object capable of being shown in an
		/// OpenGL viewport.
		/// </summary>
		public IRenderable RenderTarget { get; private set; }

		/// <summary>
		/// The camera viewpoint of the observer.
		/// </summary>
		private readonly ViewportCamera Camera;

		/// <summary>
		/// The movement component for the camera.
		/// </summary>
		private readonly CameraMovement Movement;

		/// <summary>
		/// Gets the time taken to render the last frame (in seconds).
		/// </summary>
		private float DeltaTime => (float)this.ViewportWidget.DeltaTime;

		/// <summary>
		/// Gets or sets a value indicating whether or not the viewer wants to move in the world.
		/// </summary>
		public bool WantsToMove { get; set; }

		/// <summary>
		/// Gets or sets the X position of the mouse during the last frame, relative to the <see cref="ViewportWidget"/>.
		/// </summary>
		public int InitialMouseX { get; set; }

		/// <summary>
		/// Gets or sets the Y position of the mouse during the last frame, relative to the <see cref="ViewportWidget"/>.
		/// </summary>
		public int InitialMouseY { get; set; }

		/*
			Runtime transitional OpenGL data.
		*/

		/// <summary>
		/// The OpenGL ID of the vertex array valid for the current context.
		/// </summary>
		private int VertexArrayID;

		/// <summary>
		/// Gets a value indicating whether or not this instance has been initialized and is ready
		/// to render objects.
		/// </summary>
		public bool IsInitialized
		{
			get;
			private set;
		}

		/*
			Everlook caching and static data accessors.
		*/

		/// <summary>
		/// Static reference to the configuration handler.
		/// </summary>
		private readonly EverlookConfiguration Configuration = EverlookConfiguration.Instance;

		/// <summary>
		/// The base grid, rendered underneath models.
		/// </summary>
		private BaseGrid Grid;

		/// <summary>
		/// Initializes a new instance of the <see cref="Everlook.Viewport.ViewportRenderer"/> class.
		/// </summary>
		/// <param name="viewportWidget">The widget which the viewport should be rendered to.</param>
		public ViewportRenderer(ViewportArea viewportWidget)
		{
			this.ViewportWidget = viewportWidget;
			this.Camera = new ViewportCamera();
			this.Movement = new CameraMovement(this.Camera);

			this.IsInitialized = false;
		}

		/// <summary>
		/// Initializes
		/// </summary>
		public void Initialize()
		{
			ThrowIfDisposed();

			Log.Info($"Initializing {nameof(ViewportRenderer)} and setting up default OpenGL state...");

			int numExtensions = GL.GetInteger(GetPName.NumExtensions);
			List<string> extensions = new List<string>();
			for (int i = 0; i < numExtensions; ++i)
			{
				extensions.Add(GL.GetString(StringNameIndexed.Extensions, i));
			}

			if (extensions.Contains("GL_KHR_debug"))
			{
				//GL.Enable(EnableCap.DebugOutput);
				GL.Enable(EnableCap.DebugOutputSynchronous);
				GL.DebugMessageCallback(OnGLDebugMessage, IntPtr.Zero);
			}

			// Generate the vertex array
			GL.GenVertexArrays(1, out this.VertexArrayID);
			GL.BindVertexArray(this.VertexArrayID);

			// GL.Disable(EnableCap.AlphaTest);

			// Make sure we use the depth buffer when drawing
			GL.Enable(EnableCap.DepthTest);
			GL.DepthFunc(DepthFunction.Lequal);
			GL.DepthMask(true);

			// Enable backface culling for performance reasons
			GL.Enable(EnableCap.CullFace);

			// Set a simple default blending function
			GL.Enable(EnableCap.Blend);
			GL.BlendEquation(BlendEquationMode.FuncAdd);
			GL.BlendFunc(BlendingFactorSrc.SrcAlpha, BlendingFactorDest.OneMinusSrcAlpha);

			// Initialize the viewport
			int widgetWidth = this.ViewportWidget.AllocatedWidth;
			int widgetHeight = this.ViewportWidget.AllocatedHeight;
			GL.Viewport(0, 0, widgetWidth, widgetHeight);
			GL.ClearColor
			(
				(float)this.Configuration.ViewportBackgroundColour.Red,
				(float)this.Configuration.ViewportBackgroundColour.Green,
				(float)this.Configuration.ViewportBackgroundColour.Blue,
				(float)this.Configuration.ViewportBackgroundColour.Alpha
			);

			GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

			this.Grid = new BaseGrid();
			this.Grid.Initialize();

			this.IsInitialized = true;
		}

		/// <summary>
		/// Sets the clear colour of the OpenGL viewport.
		/// </summary>
		/// <param name="colour">The new colour to use.</param>
		public void SetClearColour(RGBA colour)
		{
			GL.ClearColor
			(
				(float)colour.Red,
				(float)colour.Green,
				(float)colour.Blue,
				(float)colour.Alpha
			);
		}

		private static void OnGLDebugMessage(DebugSource source, DebugType type, int id, DebugSeverity severity, int length, IntPtr message, IntPtr userparam)
		{
			string messageContent = Marshal.PtrToStringAuto(message, length);

			Log.Debug
			(
				$"An OpenGL debug message has been received from \"{source}\" of type \"{type}\".\n{messageContent}"
			);
		}

		/// <summary>
		/// The primary rendering logic. Here, the current object is rendered using OpenGL.
		/// </summary>
		public void RenderFrame()
		{
			ThrowIfDisposed();

			lock (this.RenderTargetLock)
			{
				// Make sure the viewport is accurate for the current widget size on screen
				int widgetWidth = this.ViewportWidget.AllocatedWidth;
				int widgetHeight = this.ViewportWidget.AllocatedHeight;
				GL.Viewport(0, 0, widgetWidth, widgetHeight);
				GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

				// Calculate the current relative movement of the camera
				if (this.WantsToMove)
				{
					switch (this.RenderTarget.Projection)
					{
						case ProjectionType.Orthographic:
						{
							Calculate2DMovement();
							break;
						}
						case ProjectionType.Perspective:
						{
							Calculate3DMovement();
							break;
						}
					}
				}

				if (!this.HasRenderTarget)
				{
					return;
				}

				// Render the current object
				// Tick the actor, advancing any time-dependent behaviour
				ITickingActor tickingRenderable = this.RenderTarget as ITickingActor;
				tickingRenderable?.Tick(this.DeltaTime);

				// Update the camera with new parameters
				this.Camera.ViewportHeight = widgetHeight;
				this.Camera.ViewportWidth = widgetWidth;

				Matrix4 view = this.Camera.GetViewMatrix();
				Matrix4 projection = this.Camera.GetProjectionMatrix();

				if (this.RenderTarget.Projection == ProjectionType.Perspective)
				{
					this.Grid.Render(view, projection, this.Camera);
				}

				// Then render the visual component
				this.RenderTarget.Render(view, projection, this.Camera);
			}
		}

		/// <summary>
		/// Computes the movement of the camera in 3D space for this frame.
		/// </summary>
		private void Calculate2DMovement()
		{
			int mouseX = Mouse.GetCursorState().X;
			int mouseY = Mouse.GetCursorState().Y;

			float deltaMouseX = this.InitialMouseX - mouseX;
			float deltaMouseY = this.InitialMouseY - mouseY;

			this.Movement.Calculate2DMovement(deltaMouseX, deltaMouseY, this.DeltaTime);

			// Update the initial location for the next frame
			this.InitialMouseX = mouseX;
			this.InitialMouseY = mouseY;
		}

		/// <summary>
		/// Computes the movement of the camera in 3D space for this frame.
		/// </summary>
		private void Calculate3DMovement()
		{
			int mouseX = Mouse.GetCursorState().X;
			int mouseY = Mouse.GetCursorState().Y;

			float deltaMouseX = this.InitialMouseX - mouseX;
			float deltaMouseY = this.InitialMouseY - mouseY;

			this.Movement.Calculate3DMovement(deltaMouseX, deltaMouseY, this.DeltaTime);

			// Return the mouse to its original position
			Mouse.SetPosition(this.InitialMouseX, this.InitialMouseY);
		}

		/// <summary>
		/// Determines whether or not movement is currently disabled for the rendered object.
		/// </summary>
		/// <returns>true if movement is disabled; false otherwise.</returns>
		public bool IsMovementDisabled()
		{
			return this.RenderTarget == null || !this.RenderTarget.IsInitialized;
		}

		/// <summary>
		/// Sets the render target that is currently being rendered by the viewport renderer.
		/// </summary>
		/// <param name="inRenderable">inRenderable.</param>
		public void SetRenderTarget(IRenderable inRenderable)
		{
			ThrowIfDisposed();

			lock (this.RenderTargetLock)
			{
				// Dispose of the old render target
				this.RenderTarget?.Dispose();

				// Assign the new one
				this.RenderTarget = inRenderable;

				// Set the default camera values
				if (this.HasRenderTarget)
				{
					this.Camera.ViewportWidth = this.ViewportWidget.AllocatedWidth;
					this.Camera.ViewportHeight = this.ViewportWidget.AllocatedHeight;

					if (this.RenderTarget is IDefaultCameraPositionProvider cameraPositionProvider)
					{
						this.Camera.Position = cameraPositionProvider.DefaultCameraPosition;
					}

					this.Camera.Projection = this.RenderTarget.Projection;
				}

				this.Camera.ResetRotation();
			}
		}

		/// <summary>
		/// Throws an <see cref="ObjectDisposedException"/> if the object has been disposed.
		/// </summary>
		/// <exception cref="ObjectDisposedException">Thrown if the object is disposed.</exception>
		private void ThrowIfDisposed()
		{
			if (this.IsDisposed)
			{
				throw new ObjectDisposedException(ToString());
			}
		}

		/// <inheritdoc />
		public void Dispose()
		{
			this.IsDisposed = true;

			this.RenderTarget?.Dispose();

			GL.DeleteVertexArrays(1, ref this.VertexArrayID);
		}
	}
}
