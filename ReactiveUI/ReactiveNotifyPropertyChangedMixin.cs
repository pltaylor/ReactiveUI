using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Reactive.Disposables;
using System.Linq;
using System.Diagnostics.Contracts;
using System.Linq.Expressions;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reflection;
using System.Text;

namespace ReactiveUI
{
    class PropertySubscription
    {
        public IDisposable Subscription { get; set; }
        public Func<object, object> Getter { get; set; }
        public Func<object, IObservable<IObservedChange<object, object>>> Notifier { get; set; }
        public string Property { get; set; }
        public object CurrentSender { get; set; }
    }

    public static class ReactiveNotifyPropertyChangedMixin
    {
        /// <summary>
        /// ObservableForProperty returns an Observable representing the
        /// property change notifications for a specific property on a
        /// ReactiveObject. This method (unlike other Observables that return
        /// IObservedChange) guarantees that the Value property of
        /// the IObservedChange is set.
        /// </summary>
        /// <param name="property">An Expression representing the property (i.e.
        /// 'x => x.SomeProperty.SomeOtherProperty'</param>
        /// <param name="beforeChange">If True, the Observable will notify
        /// immediately before a property is going to change.</param>
        /// <returns>An Observable representing the property change
        /// notifications for the given property.</returns>
        public static IObservable<IObservedChange<TSender, TValue>> ObservableForPropertyOld<TSender, TValue>(
                this TSender This,
                Expression<Func<TSender, TValue>> property,
                bool beforeChange = false)
        {
            var propertyNames = new LinkedList<string>(Reflection.ExpressionToPropertyNames(property));
            var subscriptions = new LinkedList<IDisposable>(propertyNames.Select(x => (IDisposable) null));
            var ret = new Subject<IObservedChange<TSender, TValue>>();

            if (This == null) {
                throw new ArgumentNullException("Sender");
            }

            /* x => x.Foo.Bar.Baz;
             * 
             * Subscribe to This, look for Foo
             * Subscribe to Foo, look for Bar
             * Subscribe to Bar, look for Baz
             * Subscribe to Baz, publish to Subject
             * Return Subject
             * 
             * If Bar changes (notification fires on Foo), resubscribe to new Bar
             * 	Resubscribe to new Baz, publish to Subject
             * 
             * If Baz changes (notification fires on Bar),
             * 	Resubscribe to new Baz, publish to Subject
             */

            subscribeToExpressionChain(
                This, 
                buildPropPathFromNodePtr(propertyNames.First),
                This, 
                propertyNames.First, 
                subscriptions.First, 
                beforeChange, 
                ret);

            return Observable.Create<IObservedChange<TSender, TValue>>(x => {
                var disp = ret.Subscribe(x);
                return () => {
                    subscriptions.ForEach(y => y.Dispose());
                    disp.Dispose();
                };
            });
        }

        public static IObservable<IObservedChange<TSender, object>> ObservableForProperty<TSender>(
            this TSender This,
            string[] property,
            bool beforeChange = false)
        {
            var types = Reflection.GetTypesForPropChain(This.GetType(), property);
            var slots = property.Zip(types, (prop, type) => {
                var notifFactory = notifyFactoryCache2.Get(Tuple.Create(type, beforeChange));

                return new PropertySubscription() {
                    Property = prop, Getter = Reflection.GetValueFetcherForProperty(type, prop),
                    Notifier = obj => notifFactory.GetNotificationForProperty(obj, prop, beforeChange),
                    Subscription = Disposable.Empty, CurrentSender = null,
                };
            });

            var slotList = new LinkedList<PropertySubscription>(slots);
            var ret = new Subject<IObservedChange<TSender, object>>();
            var propNameString = String.Join(".", slotList.Select(x => x.Property));

            if (This == null) {
                throw new ArgumentNullException("Sender");
            }

            return Observable.Create<IObservedChange<TSender, object>>(x => {
                var disp = ret.Subscribe(x);

                return Disposable.Create(() => {
                    disp.Dispose();
                    slotList.ForEach(y => y.Subscription.Dispose());
                });
            });
        }

        public static IObservable<IObservedChange<TSender, TValue>> ObservableForProperty<TSender, TValue>(
            this TSender This,
            Expression<Func<TSender, TValue>> property,
            bool beforeChange = false)
        {
            var chain = Reflection.ExpressionToPropertyNames(property).Concat(new string[] {null});
            var types = new[] {typeof (TSender)}.Concat(Reflection.GetTypesForPropChain(This.GetType(), chain.SkipLast(1)));

            var slots = chain.Zip(types, (prop, type) => {
                var notifFactory = notifyFactoryCache2.Get(Tuple.Create(type, beforeChange));
                var getter = prop != null ? Reflection.GetValueFetcherForProperty(type, prop) : null;
                var currentProp = prop;

                return new PropertySubscription() {
                    Property = prop, Getter = getter,
                    Notifier = obj => notifFactory.GetNotificationForProperty(obj, currentProp, beforeChange),
                    Subscription = Disposable.Empty, CurrentSender = null,
                };
            });

            var slotList = new LinkedList<PropertySubscription>(slots);
            var ret = new Subject<IObservedChange<TSender, TValue>>();
            var propNameString = String.Join(".", slotList.Select(x => x.Property));

            if (This == null) {
                throw new ArgumentNullException("Sender");
            }

            subscribeToExpressionChain2(This, propNameString, ret, This, slotList.First);

            return Observable.Create<IObservedChange<TSender, TValue>>(x => {
                var disp = ret.Subscribe(x);

                return Disposable.Create(() => {
                    disp.Dispose();
                    slotList.ForEach(y => y.Subscription.Dispose());
                });
            });
        }

        static void subscribeToExpressionChain2<TSender, TValue>(
            TSender rootObject,
            string propNameString,
            Subject<IObservedChange<TSender, TValue>> target,
            object currentSender,
            LinkedListNode<PropertySubscription> slotList)
        {
            var current = slotList;
            var sender = currentSender;
            while (current != null) {
                current.Value.Subscription.Dispose(); 
                current.Value.Subscription = Disposable.Empty; 
                current.Value.CurrentSender = null;
                current = current.Next;
            }

            current = slotList;
            while (current.Next != null) {
                var propSub = current.Value;
                var item = current;

                propSub.CurrentSender = sender;
                propSub.Subscription = propSub.Notifier(currentSender).Subscribe(_ => 
                    subscribeToExpressionChain2(rootObject, propNameString, target, propSub.CurrentSender, item));

                sender = propSub.Getter(sender);
                current = (currentSender != null ? current.Next : null);
            }

            if (current == null) return;
            
            current.Value.Subscription = current.Value.Notifier(currentSender).Subscribe(_ =>
                target.OnNext(new ObservedChange<TSender, TValue>() {
                    Sender = rootObject, PropertyName = propNameString,
                    Value = (TValue)current.Previous.Value.Getter(current.Previous.Value.CurrentSender),
                }));
        }


        /// <summary>
        /// ObservableForPropertyDynamic returns an Observable representing the
        /// property change notifications for a specific property on a
        /// ReactiveObject. This method (unlike other Observables that return
        /// IObservedChange) guarantees that the Value property of
        /// the IObservedChange is set.
        /// </summary>
        /// <param name="property">An Expression representing the property (i.e.
        /// 'x => x.SomeProperty.SomeOtherProperty'</param>
        /// <param name="beforeChange">If True, the Observable will notify
        /// immediately before a property is going to change.</param>
        /// <returns>An Observable representing the property change
        /// notifications for the given property.</returns>
        public static IObservable<IObservedChange<TSender, object>> ObservableForPropertyOld<TSender>(
                this TSender This,
                string[] property,
                bool beforeChange = false)
        {
            var propertyNames = new LinkedList<string>(property);
            var subscriptions = new LinkedList<IDisposable>(propertyNames.Select(x => (IDisposable) null));

            var ret = new Subject<IObservedChange<TSender, object>>();

            if (This == null) {
                throw new ArgumentNullException("Sender");
            }

            /* x => x.Foo.Bar.Baz;
             * 
             * Subscribe to This, look for Foo
             * Subscribe to Foo, look for Bar
             * Subscribe to Bar, look for Baz
             * Subscribe to Baz, publish to Subject
             * Return Subject
             * 
             * If Bar changes (notification fires on Foo), resubscribe to new Bar
             * 	Resubscribe to new Baz, publish to Subject
             * 
             * If Baz changes (notification fires on Bar),
             * 	Resubscribe to new Baz, publish to Subject
             */

            subscribeToExpressionChain(
                This, 
                buildPropPathFromNodePtr(propertyNames.First),
                This, 
                propertyNames.First, 
                subscriptions.First, 
                beforeChange, 
                ret);

            return Observable.Create<IObservedChange<TSender, object>>(x => {
                var disp = ret.Subscribe(x);
                return () => {
                    subscriptions.ForEach(y => y.Dispose());
                    disp.Dispose();
                };
            });
        }

        /// <summary>
        /// ObservableForProperty returns an Observable representing the
        /// property change notifications for a specific property on a
        /// ReactiveObject, running the IObservedChange through a Selector
        /// function.
        /// </summary>
        /// <param name="property">An Expression representing the property (i.e.
        /// 'x => x.SomeProperty'</param>
        /// <param name="selector">A Select function that will be run on each
        /// item.</param>
        /// <param name="beforeChange">If True, the Observable will notify
        /// immediately before a property is going to change.</param>
        /// <returns>An Observable representing the property change
        /// notifications for the given property.</returns>
        public static IObservable<TRet> ObservableForProperty<TSender, TValue, TRet>(
                this TSender This, 
                Expression<Func<TSender, TValue>> property, 
                Func<TValue, TRet> selector, 
                bool beforeChange = false)
            where TSender : class
        {           
            Contract.Requires(selector != null);
            return This.ObservableForProperty(property, beforeChange).Select(x => selector(x.Value));
        }

        static void subscribeToExpressionChain<TSender, TValue>(
                TSender origSource,
                string origPath,
                object source,
                LinkedListNode<string> propertyNames, 
                LinkedListNode<IDisposable> subscriptions, 
                bool beforeChange,
                Subject<IObservedChange<TSender, TValue>> subject
            )
        {
            var current = propertyNames;
            var currentSub = subscriptions;
            object currentObj = source;
            ObservedChange<TSender, TValue> obsCh;

            while(current.Next != null) {
                Func<object, object> getter = null;

                if (currentObj != null) {
                    getter = Reflection.GetValueFetcherForProperty(currentObj.GetType(), current.Value);

                    if (getter == null) {
                        subscriptions.List.Where(x => x != null).ForEach(x => x.Dispose());
                        throw new ArgumentException(String.Format("Property '{0}' does not exist in expression", current.Value));
                    }

                    var capture = new {current, currentObj, getter, currentSub};

                    var toDispose = new IDisposable[2];

                    var valGetter = new ObservedChange<object, TValue>() {
                        Sender = capture.currentObj,
                        PropertyName = buildPropPathFromNodePtr(capture.current),
                        Value = default(TValue),
                    };

                    TValue prevVal = default(TValue);
                    bool prevValSet = valGetter.TryGetValue(out prevVal);

                    toDispose[0] = notifyForProperty(currentObj, capture.current.Value, true).Subscribe(x => {
                        prevValSet = valGetter.TryGetValue(out prevVal);
                    });

                    toDispose[1] = notifyForProperty(currentObj, capture.current.Value, false).Subscribe(x => {
                        subscribeToExpressionChain(origSource, origPath, capture.getter(capture.currentObj), capture.current.Next, capture.currentSub.Next, beforeChange, subject);

                        TValue newVal;
                        if (!valGetter.TryGetValue(out newVal)) {
                            return;
                        }
                        
                        if (prevValSet && EqualityComparer<TValue>.Default.Equals(prevVal, newVal)) {
                            return;
                        }

                        obsCh = new ObservedChange<TSender, TValue>() {
                            Sender = origSource,
                            PropertyName = origPath,
                            Value = default(TValue),
                        };

                        TValue obsChVal;
                        if (obsCh.TryGetValue(out obsChVal)) {
                            obsCh.Value = obsChVal;
                            subject.OnNext(obsCh);
                        }
                    });

                    currentSub.Value = Disposable.Create(() => { toDispose[0].Dispose(); toDispose[1].Dispose(); });
                }

                current = current.Next;
                currentSub = currentSub.Next;
                currentObj = getter != null ? getter(currentObj) : null;
            }

            if (currentSub.Value != null) {
                currentSub.Value.Dispose();
            }

            if (currentObj == null) {
                return;
            }

            var propName = current.Value;
            var finalGetter = Reflection.GetValueFetcherForProperty(currentObj.GetType(), current.Value);

            currentSub.Value = notifyForProperty(currentObj, propName, beforeChange).Subscribe(x => {
                obsCh = new ObservedChange<TSender, TValue>() {
                    Sender = origSource,
                    PropertyName = origPath,
                    Value = (TValue)finalGetter(currentObj),
                };

                subject.OnNext(obsCh);
            });
        }

        static readonly MemoizingMRUCache<Tuple<Type, bool>, ICreatesObservableForProperty> notifyFactoryCache2 =
            new MemoizingMRUCache<Tuple<Type, bool>, ICreatesObservableForProperty>((t, _) => {
                return RxApp.GetAllServices<ICreatesObservableForProperty>()
                    .Aggregate(Tuple.Create(0, (ICreatesObservableForProperty)null), (acc, x) => {
                        int score = x.GetAffinityForObject(t.Item1, t.Item2);
                        return (score > acc.Item1 && score > 0) ? Tuple.Create(score, x) : acc;
                    }).Item2;
            }, 50);

        static readonly MemoizingMRUCache<Type, ICreatesObservableForProperty> notifyFactoryCache =
            new MemoizingMRUCache<Type, ICreatesObservableForProperty>((t, _) => {
                return RxApp.GetAllServices<ICreatesObservableForProperty>()
                    .Aggregate(Tuple.Create(0, (ICreatesObservableForProperty)null), (acc, x) => {
                        int score = x.GetAffinityForObject(t);
                        return (score > acc.Item1) ? Tuple.Create(score, x) : acc;
                    }).Item2;
            }, 50);

        static IObservable<IObservedChange<object, object>> notifyForProperty(object sender, string propertyName, bool beforeChange)
        {
            var result = notifyFactoryCache.Get(sender.GetType(), beforeChange);
            if (result == null) {
                throw new Exception(
                    String.Format("Couldn't find a ICreatesObservableForProperty for {0}. This should never happen, your service locator is probably broken.", 
                    sender.GetType()));
            }
            
            return result.GetNotificationForProperty(sender, propertyName, beforeChange);
        }

        static string buildPropPathFromNodePtr(LinkedListNode<string> node)
        {
            var ret = new StringBuilder();
            var current = node;

            while(current.Next != null) {
                ret.Append(current.Value);
                ret.Append('.');
                current = current.Next;
            }

            ret.Append(current.Value);
            return ret.ToString();
        }
    }
}

// vim: tw=120 ts=4 sw=4 et :
