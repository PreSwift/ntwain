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
    class CapVM
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


            var capName = cap.ToString();
            var wrapProperty = ds.Capabilities.GetType().GetProperty(capName);
            if (wrapProperty != null)
            {
                _wrapper = wrapProperty.GetGetMethod().Invoke(ds.Capabilities, null);
                var wrapperType = _wrapper.GetType();
                _getMethod = wrapperType.GetMethod("GetValues");
                _getCurrentMethod = wrapperType.GetMethod("GetCurrent");
                _setMethod = wrapperType.GetMethods().FirstOrDefault(m => m.Name == "SetValue");
                _resetMethod = wrapperType.GetMethods().FirstOrDefault(m => m.Name == "Reset");
            }

            var supportTest = ds.Capabilities.QuerySupport(cap);
            if (supportTest.HasValue)
            {
                Supports = supportTest.Value;
            }
            else
            {
                if (_wrapper != null)
                {
                    var wrapperType = _wrapper.GetType();
                    QuerySupports? supports = (QuerySupports?)wrapperType.GetProperty("SupportedActions").GetGetMethod().Invoke(_wrapper, null);
                    Supports = supports.GetValueOrDefault();
                }
            }

        }

        public IEnumerable Get()
        {
            if (_getMethod == null)
            {
                return _ds.Capabilities.GetValues(Cap);
            }
            return _getMethod.Invoke(_wrapper, null) as IEnumerable;
        }
        public object GetCurrent()
        {
            if (_getMethod == null)
            {
                return _ds.Capabilities.GetCurrent(Cap);
            }
            return _getCurrentMethod.Invoke(_wrapper, null);
        }
        public void Set(object value)
        {
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

        public QuerySupports Supports { get; private set; }

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
