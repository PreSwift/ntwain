using NTwain;
using NTwain.Data;
using System;
using System.Collections;
using System.Linq;
using System.Reflection;

namespace Sample.WPF
{
    /// <summary>
    /// Wraps a capability as a view model.
    /// </summary>
    class CapVM : GalaSoft.MvvmLight.ViewModelBase
    {
        DataSource _ds;
        object _wrapper;
        MethodInfo _getMethod;
        MethodInfo _getCurrentMethod;
        MethodInfo _setMethod;
        MethodInfo _resetMethod;

        public CapVM(DataSource ds, CapabilityId cap)
        {
            _ds = ds;
            Cap = cap;
        }

        bool _wrapperInitialized;

        // Wrapper construction itself queries the driver.
        void EnsureWrapper()
        {
            if (_wrapperInitialized)
                return;
            var capName = Cap.ToString();
            var wrapProperty = _ds.Capabilities.GetType().GetProperty(capName);
            if (wrapProperty != null)
            {
                _wrapper = wrapProperty.GetGetMethod().Invoke(_ds.Capabilities, null);
                var wrapperType = _wrapper.GetType();
                _getMethod = wrapperType.GetMethod("GetValues");
                _getCurrentMethod = wrapperType.GetMethod("GetCurrent");
                _setMethod = wrapperType.GetMethods().FirstOrDefault(m => m.Name == "SetValue");
                _resetMethod = wrapperType.GetMethods().FirstOrDefault(m => m.Name == "Reset");
            }
            _wrapperInitialized = true;
        }

        public IEnumerable Get()
        {
            EnsureWrapper();
            if (_getMethod == null)
            {
                return _ds.Capabilities.GetValues(Cap);
            }
            return _getMethod.Invoke(_wrapper, null) as IEnumerable;
        }
        public object GetCurrent()
        {
            EnsureWrapper();
            if (_getMethod == null)
            {
                return _ds.Capabilities.GetCurrent(Cap);
            }
            return _getCurrentMethod.Invoke(_wrapper, null);
        }
        public void Set(object value)
        {
            EnsureWrapper();
            if (_setMethod != null && value != null)
            {
                _setMethod.Invoke(_wrapper, new object[] { value });
            }
        }

        public void Set(Func<TWOneValue> hock)
        {
            TWOneValue tWOneValue = hock();
            if (tWOneValue != null)
            {
                using (var cap = new TWCapability(Cap, tWOneValue))
                {
                    _ds.DGControl.Capability.Set(cap);
                }
            }
        }

        public void Reset()
        {
            EnsureWrapper();
            if (_resetMethod != null)
            {
                _resetMethod.Invoke(_wrapper, null);
            }
        }

        public CapabilityId Cap { get; private set; }

        public string Name
        {
            get
            {
                if (Cap > CapabilityId.CustomBase)
                {
                    return "[CustomBase]+" + ((int)Cap - (int)CapabilityId.CustomBase);
                }
                return Cap.ToString();
            }
        }

        QuerySupports? _supports;

        // Rendering the list must never query every capability in the driver.
        public string SupportsDisplay { get { return _supports.HasValue ? _supports.Value.ToString() : "选中后读取"; } }

        public QuerySupports Supports
        {
            get
            {
                if (!_supports.HasValue)
                {
                    if (!_ds.IsOpen)
                        return default(QuerySupports);
                    var supportTest = _ds.Capabilities.QuerySupport(Cap);
                    if (supportTest.HasValue)
                    {
                        _supports = supportTest.Value;
                    }
                    else
                    {
                        EnsureWrapper();
                        if (_wrapper != null)
                        {
                            var wrapperType = _wrapper.GetType();
                            QuerySupports? supports = (QuerySupports?)wrapperType.GetProperty("SupportedActions").GetGetMethod().Invoke(_wrapper, null);
                            _supports = supports.GetValueOrDefault();
                        }
                    }
                    if (!_supports.HasValue)
                        _supports = default(QuerySupports);
                    RaisePropertyChanged(() => SupportsDisplay);
                }
                return _supports.Value;
            }
        }

        public override string ToString()
        {
            return Name;
        }

        public override bool Equals(object obj)
        {
            //       
            // See the full list of guidelines at
            //   http://go.microsoft.com/fwlink/?LinkID=85237  
            // and also the guidance for operator== at
            //   http://go.microsoft.com/fwlink/?LinkId=85238
            //

            if (obj == null || GetType() != obj.GetType())
            {
                return false;
            }

            CapVM objVM = obj as CapVM;
            if (objVM._ds != null && _ds != null && objVM._ds == _ds && objVM.Cap == Cap)
            {
                return true;
            }

            return base.Equals(obj);
        }

        // override object.GetHashCode
        public override int GetHashCode()
        {
            if (_ds != null)
            {
                return Cap.GetHashCode() + _ds.GetHashCode();
            }
            return base.GetHashCode();
        }

    }
}
