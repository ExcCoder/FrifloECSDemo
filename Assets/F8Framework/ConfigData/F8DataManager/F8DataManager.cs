/*
 *   Bootstrap stub for initial compilation before first F8 run.
 *   Running F8 (开发工具/1: F8Run) will regenerate this file automatically.
 */

using System;
using System.Collections;
using System.Collections.Generic;
using F8Framework.Core;
using UnityEngine.Scripting;

namespace F8Framework.F8ExcelDataClass
{
	public class F8DataManager : ModuleSingleton<F8DataManager>, IModule
	{
		public string VariantName;

		[Preserve]
		public IEnumerator LoadLocalizedStringsIEnumerator(Action onLoadComplete = null)
		{
			onLoadComplete?.Invoke();
			yield break;
		}

		[Preserve]
		public Dictionary<int, object> GetLocalizedStrings()
		{
			return null;
		}

		public void OnInit(object createParam)
		{
		}

		public void OnUpdate()
		{
		}

		public void OnLateUpdate()
		{
		}

		public void OnFixedUpdate()
		{
		}

		public void OnTermination()
		{
			VariantName = null;
			base.Destroy();
		}
	}
}
